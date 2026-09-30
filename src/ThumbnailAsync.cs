using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace SaveOpt
{
    internal sealed class ThumbJob
    {
        internal string Path;
        internal Color32[] Pixels;
        internal int Width;
        internal int Height;
        internal byte[] Reference;
    }

    internal static class ThumbnailAsync
    {
        private const float PreviewScale = 0.5f;

        // AsyncGPUReadback 返回的数据行序：D3D 自上而下（false），OpenGL 自下而上（true）
        private const bool FlipRowsForPNG = false;

        private static readonly byte[] Sentinel = new byte[0];

        private static AccessTools.FieldRef<Timelapser, Vector2Int> previewRes;
        private static AccessTools.FieldRef<Timelapser, bool> previewScreenshotField;
        private static AccessTools.FieldRef<Timelapser, string> previewSaveGamePathField;
        private static AccessTools.FieldRef<Timelapser, RenderTexture> bufferRenderTextureField;
        private static Vector2Int previewOriginal;
        private static Vector2Int previewApplied;
        private static bool previewHave;
        private static long previewShrinks;
        private static long rtReuses;
        private static long rtRebuilds;

        // ───────── 自动 / 手动 ─────────

        private static bool currentSaveIsAuto;

        // ★ 新增：本局最近一次手动存档的 .sav 完整路径。
        // UI 显示自动存档条目且其 png 不存在时，LoadColonyPreviewPatch 会把它
        // 作为 savePath 交给 RetireColonyUtility.LoadColonyPreview，从而显示这张手动存档的 png。
        private static string lastManualSavePath;

        // ───────── 统计 ─────────

        private static long previewCaptures;
        private static long previewSkips;
        private static long asyncRequests;
        private static long asyncCompleted;
        private static long asyncFailed;
        private static long fallbacks;

        // ───────── 后台编码线程（timelapse 回退路径）─────────

        private static readonly object Gate = new object();
        private static readonly Queue<ThumbJob> Queue = new Queue<ThumbJob>();
        private static readonly AutoResetEvent Signal = new AutoResetEvent(false);

        private static Thread worker;
        private static volatile bool stopping;
        private static bool enabled;

        private static bool verified;
        private static bool flipRows;
        private static bool mismatch;

        private static readonly Queue<ThumbJob> Pending = new Queue<ThumbJob>();

        private static long jobs;
        private static long lastEncodeMs;
        private static long lastMainMs;
        private static long refEncodeMs;
        private static long totalEncodeMs;
        private static long bytes;
        private static string lastNote = "";

        internal static bool Apply(HarmonyLib.Harmony harmony)
        {
            MethodInfo target = AccessTools.Method(typeof(Timelapser), "WriteToPng");
            MethodInfo writeAll = AccessTools.Method(typeof(File), "WriteAllBytes", new[] { typeof(string), typeof(byte[]) });
            if (target == null || writeAll == null)
            {
                Debug.LogWarning("[更好的存档] 找不到 Timelapser.WriteToPng 或 File.WriteAllBytes，缩略图后台化未挂载");
                return false;
            }

            try
            {
                previewScreenshotField = AccessTools.FieldRefAccess<Timelapser, bool>("previewScreenshot");
                previewSaveGamePathField = AccessTools.FieldRefAccess<Timelapser, string>("previewSaveGamePath");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[更好的存档] 无法访问 Timelapser 私有字段，AsyncGPUReadback 路径不可用: " + e.Message);
                previewScreenshotField = null;
                previewSaveGamePathField = null;
            }

            try
            {
                harmony.Patch(target,
                    prefix: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(ThumbnailAsync), "WriteToPng_Prefix")),
                    transpiler: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(ThumbnailAsync), "Transpile")));
            }
            catch (Exception e)
            {
                Debug.LogError("[更好的存档] WriteToPng 补丁挂载失败: " + e.Message);
                return false;
            }

            try
            {
                harmony.Patch(writeAll, prefix: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(ThumbnailAsync), "WriteAllBytes_Prefix")));
            }
            catch (Exception e)
            {
                Debug.LogError("[更好的存档] File.WriteAllBytes 挂载失败: " + e.Message);
                return false;
            }

            ApplyPreviewScale(harmony);

            Start();
            enabled = true;

            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                Debug.LogWarning("[更好的存档] 当前平台不支持 AsyncGPUReadback，预览图会退回同步路径");
            }
            else
            {
                Diag.Trace("[更好的存档] 缩略图捕获：手动存档走 AsyncGPUReadback（主线程不阻塞），自动存档跳过截图");
            }
            return true;
        }

        private static int swappedInjected;

        private static void ApplyPreviewScale(HarmonyLib.Harmony harmony)
        {
            try
            {
                MethodInfo refresh = AccessTools.Method(typeof(Timelapser), "RefreshRenderTextureSize");
                MethodInfo colony = AccessTools.Method(typeof(Timelapser), "SaveColonyPreview");
                if (refresh == null || colony == null)
                {
                    Debug.LogWarning("[更好的存档] 找不到预览图相关方法，预览图跳渲未启用");
                    return;
                }
                previewRes = AccessTools.FieldRefAccess<Timelapser, Vector2Int>("previewScreenshotResolution");
                if (previewRes == null)
                {
                    Debug.LogWarning("[更好的存档] 找不到 Timelapser.previewScreenshotResolution，预览图未启用");
                    return;
                }

                bufferRenderTextureField = AccessTools.FieldRefAccess<Timelapser, RenderTexture>("bufferRenderTexture");
                if (bufferRenderTextureField == null)
                {
                    Debug.LogWarning("[更好的存档] 找不到 Timelapser.bufferRenderTexture，预览 RT 复用未启用");
                }

                harmony.Patch(colony, prefix: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(ThumbnailAsync), "SaveColonyPreview_Prefix")));
                harmony.Patch(refresh, prefix: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(ThumbnailAsync), "Refresh_Prefix")));

                Diag.Trace("[更好的存档] 预览图分辨率降至 " + (PreviewScale * 100f).ToString("F0")
                    + "%，自动存档默认跳过截图，UI 显示时重定向到最近一次手动存档的图");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[更好的存档] 预览图跳渲挂载失败: " + e.Message);
            }
        }

        // ───────── 自动 / 手动 标记 ─────────

        internal static void NoteSaveKind(bool isAuto)
        {
            currentSaveIsAuto = isAuto;
        }

        // ★ 新增：供 LoadColonyPreviewPatch 读取
        internal static string GetLastManualSavePath()
        {
            return lastManualSavePath;
        }

        // ───────── AsyncGPUReadback 路径（手动存档 / 自动存档+选项开启）─────────

        public static bool WriteToPng_Prefix(Timelapser __instance, RenderTexture renderTex, int world_id)
        {
            if (previewScreenshotField == null || previewSaveGamePathField == null)
            {
                return true;
            }

            bool isPreview;
            try
            {
                isPreview = previewScreenshotField(__instance);
            }
            catch (Exception)
            {
                return true;
            }

            if (!isPreview)
            {
                return true;
            }

            string previewPath;
            try
            {
                previewPath = previewSaveGamePathField(__instance);
            }
            catch (Exception)
            {
                return true;
            }

            if (string.IsNullOrEmpty(previewPath))
            {
                return true;
            }

            string pngPath;
            try
            {
                pngPath = Path.ChangeExtension(previewPath, ".png");
            }
            catch (Exception)
            {
                return true;
            }

            if (string.IsNullOrEmpty(pngPath) || renderTex == null)
            {
                return true;
            }

            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                return true;
            }

            int w = renderTex.width;
            int h = renderTex.height;
            if (w <= 0 || h <= 0)
            {
                return true;
            }

            try
            {
                asyncRequests++;
                AsyncGPUReadback.Request(renderTex, 0, TextureFormat.RGBA32,
                    request => ThumbnailAsync.OnReadbackComplete(request, w, h, pngPath));
            }
            catch (Exception e)
            {
                asyncFailed++;
                Debug.LogWarning("[更好的存档] AsyncGPUReadback 请求失败，回退同步: " + e.Message);
                return true;
            }

            return false;
        }

        private static void OnReadbackComplete(AsyncGPUReadbackRequest request, int width, int height, string pngPath)
        {
            if (request.hasError)
            {
                asyncFailed++;
                Debug.LogError("[更好的存档] AsyncGPUReadback 完成时出错: " + pngPath);
                return;
            }

            try
            {
                NativeArray<byte> data = request.GetData<byte>();
                int expected = width * height * 4;

                if (data.Length < expected)
                {
                    asyncFailed++;
                    Debug.LogError("[更好的存档] AsyncGPUReadback 数据长度不符: " + data.Length + " < " + expected);
                    return;
                }

                byte[] raw = new byte[expected];
                NativeArray<byte>.Copy(data, raw);

                asyncCompleted++;

                ThreadPool.QueueUserWorkItem(_ => ThumbnailAsync.EncodeAndWrite(raw, width, height, pngPath));
            }
            catch (Exception e)
            {
                asyncFailed++;
                Debug.LogError("[更好的存档] AsyncGPUReadback 数据处理失败: " + e.Message);
            }
        }

        private static void EncodeAndWrite(byte[] rgba, int width, int height, string pngPath)
        {
            try
            {
                long t0 = (long)Now();

                byte[] source = FlipRowsForPNG
                    ? ThumbnailAsync.FlipRows(rgba, width, height)
                    : rgba;

                byte[] png = ImageConversion.EncodeArrayToPNG(
                    source,
                    GraphicsFormat.R8G8B8A8_UNorm,
                    (uint)width,
                    (uint)height,
                    (uint)(width * 4));

                File.WriteAllBytes(pngPath, png);

                long ms = (long)Now() - t0;

                lock (Gate)
                {
                    jobs++;
                    lastEncodeMs = ms;
                    totalEncodeMs += ms;
                    bytes += png.Length;
                }

                if (jobs == 1)
                {
                    Diag.Trace("[更好的存档] AsyncGPUReadback 首张 " + width + "x" + height
                        + "，线程池编码+写盘 " + ms + " ms，" + png.Length + " 字节");
                }
            }
            catch (Exception e)
            {
                asyncFailed++;
                Debug.LogError("[更好的存档] 线程池编码失败: " + pngPath + " : " + e.Message);
            }
        }

        private static byte[] FlipRows(byte[] src, int width, int height)
        {
            int rowBytes = width * 4;
            byte[] dst = new byte[src.Length];
            for (int y = 0; y < height; y++)
            {
                Buffer.BlockCopy(src, y * rowBytes, dst, (height - 1 - y) * rowBytes, rowBytes);
            }
            return dst;
        }

        public static bool SaveColonyPreview_Prefix(string __0)
        {
            // 自动存档 + 选项未开：跳过截图。不生成 png，之后 UI 显示时由 LoadColonyPreviewPatch 重定向。
            if (currentSaveIsAuto && !BetterSaveSettings.AutoSaveThumbnail) //自动保存禁止截图（可在选项里打开）
            {
                previewSkips++;
                return false;
            }

            // ★ 手动存档：正常截图，并记录 .sav 完整路径，作为后续自动存档 UI 显示的重定向源。
            //   自动存档（选项已开时）不记录，保证 lastManualSavePath 恒为"最近一次手动存档"。
            if (!currentSaveIsAuto)
            {
                try
                {
                    lastManualSavePath = __0;
                }
                catch (Exception)
                {
                    lastManualSavePath = null;
                }
            }

            previewCaptures++;
            Diag.Trace("[更好的存档] " + (currentSaveIsAuto ? "自动(选项已开)" : "手动")
                + "存档：真实捕获缩略图 -> " + Path.GetFileName(__0));
            return true;
        }

        // ★ 11b6670：签名 void → bool，末尾加 RT 复用逻辑
        public static bool Refresh_Prefix(Timelapser __instance)
        {
            if (previewRes == null)
            {
                return true;
            }

            Vector2Int current = previewRes(__instance);
            if (!previewHave || current != previewApplied)
            {
                previewOriginal = current;
                previewHave = true;
            }
            int w = Mathf.Max(64, (int)(previewOriginal.x * PreviewScale));
            int h = Mathf.Max(64, (int)(previewOriginal.y * PreviewScale));
            Vector2Int scaled = new Vector2Int(w, h);
            if (current != scaled)
            {
                previewRes(__instance) = scaled;
                previewShrinks++;
                if (previewShrinks == 1)
                {
                    Diag.Trace("[更好的存档] 预览图分辨率 " + previewOriginal.x + " x " + previewOriginal.y
                        + " -> " + w + " x " + h);
                }
                previewApplied = scaled;
            }

            // RT 复用：尺寸 + 名字都对得上时跳过原版 Destroy + new。
            // 原版流程每次截图周期都 DestroyRenderTexture() + new RenderTexture(...)，
            // 稳态下这属于纯浪费；复用后只在分辨率变化时重建。
            if (bufferRenderTextureField == null || previewScreenshotField == null)
            {
                return true;
            }

            bool isPreview;
            try
            {
                isPreview = previewScreenshotField(__instance);
            }
            catch (Exception)
            {
                return true;
            }

            Vector2Int wantSize;
            string wantName;
            if (isPreview)
            {
                wantSize = previewRes(__instance);
                wantName = "Timelapser.PreviewScreenshot";
            }
            else if (SaveGame.Instance != null && SaveGame.Instance.TimelapseResolution.x > 0)
            {
                wantSize = new Vector2Int(
                    SaveGame.Instance.TimelapseResolution.x,
                    SaveGame.Instance.TimelapseResolution.y);
                wantName = "Timelapser.Timelapse";
            }
            else
            {
                // 原版两个 if 都不满足时什么都不做，这里跳过原版效果相同
                return false;
            }

            RenderTexture currentRt;
            try
            {
                currentRt = bufferRenderTextureField(__instance);
            }
            catch (Exception)
            {
                return true;
            }

            if (currentRt != null
                && currentRt.width == wantSize.x
                && currentRt.height == wantSize.y
                && currentRt.name == wantName)
            {
                rtReuses++;
                return false;
            }

            if (currentRt != null)
            {
                try
                {
                    currentRt.DestroyRenderTexture();
                }
                catch (Exception)
                {
                }
            }

            RenderTexture newRt = new RenderTexture(wantSize.x, wantSize.y, 32, RenderTextureFormat.ARGB32);
            newRt.name = wantName;
            bufferRenderTextureField(__instance) = newRt;
            rtRebuilds++;

            if (rtRebuilds == 1)
            {
                Diag.Trace(string.Concat(new string[]
                {
                    "[更好的存档] 预览 RT 首次创建 ",
                    wantSize.x.ToString(),
                    " x ",
                    wantSize.y.ToString(),
                    " (",
                    wantName,
                    ")"
                }));
            }

            return false;
        }

        public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo encodeToPng = AccessTools.Method(typeof(ImageConversion), "EncodeToPNG", new[] { typeof(Texture2D) });
            MethodInfo capture = AccessTools.Method(typeof(ThumbnailAsync), "Capture");
            swappedInjected = 0;
            if (encodeToPng == null || capture == null) return instructions;

            var list = new List<CodeInstruction>(instructions);
            for (int i = 0; i < list.Count; i++)
            {
                CodeInstruction ins = list[i];
                if (ins.opcode != OpCodes.Call && ins.opcode != OpCodes.Callvirt) continue;
                MethodInfo m = ins.operand as MethodInfo;
                if (m == null || m != encodeToPng) continue;

                var rep = new CodeInstruction(OpCodes.Call, capture);
                foreach (Label lb in ins.labels) rep.labels.Add(lb);
                foreach (ExceptionBlock eb in ins.blocks) rep.blocks.Add(eb);
                list[i] = rep;
                swappedInjected++;
            }
            return list;
        }

        public static byte[] Capture(Texture2D tex)
        {
            if (!enabled || tex == null) return ImageConversion.EncodeToPNG(tex);

            double t0 = Now();
            try
            {
                var job = new ThumbJob();
                job.Width = tex.width;
                job.Height = tex.height;
                job.Pixels = tex.GetPixels32();
                if (!verified && !mismatch)
                {
                    double r0 = Now();
                    job.Reference = ImageConversion.EncodeToPNG(tex);
                    refEncodeMs = (long)(Now() - r0);
                }
                lastMainMs = (long)(Now() - t0);
                lock (Gate) Pending.Enqueue(job);
                return Sentinel;
            }
            catch (Exception e)
            {
                enabled = false;
                fallbacks++;
                Debug.LogError("[更好的存档] 缩略图像素抓取失败，缩略图后台化已停用: " + e.Message);
                return ImageConversion.EncodeToPNG(tex);
            }
        }

        public static bool WriteAllBytes_Prefix(string path, byte[] bytes)
        {
            if (!ReferenceEquals(bytes, Sentinel)) return true;

            ThumbJob job = null;
            lock (Gate)
            {
                if (Pending.Count > 0) job = Pending.Dequeue();
            }
            if (job == null)
            {
                fallbacks++;
                return true;
            }

            job.Path = path;
            lock (Gate) Queue.Enqueue(job);
            Signal.Set();
            return false;
        }

        private static void Start()
        {
            if (worker != null) return;
            stopping = false;
            worker = new Thread(Loop);
            worker.IsBackground = true;
            worker.Name = "SaveOptThumbnail";
            worker.Start();
        }

        private static void Loop()
        {
            while (!stopping)
            {
                Signal.WaitOne();
                while (true)
                {
                    ThumbJob job;
                    lock (Gate)
                    {
                        if (Queue.Count == 0) break;
                        job = Queue.Dequeue();
                    }
                    Run(job);
                }
            }
        }

        private static void Run(ThumbJob job)
        {
            try
            {
                double t0 = Now();
                bool flip = flipRows;
                byte[] png = Encode(job, flip);

                if (job.Reference != null && !verified && !mismatch)
                {
                    if (Same(png, job.Reference))
                    {
                        flip = false;
                        verified = true;
                    }
                    else
                    {
                        byte[] flipped = Encode(job, true);
                        if (Same(flipped, job.Reference))
                        {
                            flip = true;
                            flipRows = true;
                            verified = true;
                            png = flipped;
                        }
                        else
                        {
                            mismatch = true;
                            enabled = false;
                            png = job.Reference;
                            lastNote = "★★ 字节不一致，已永久回退（本张用主线程结果写入）";
                        }
                    }
                }

                long ms = (long)(Now() - t0);
                lastEncodeMs = ms;
                totalEncodeMs += ms;
                File.WriteAllBytes(job.Path, png);
                jobs++;
                bytes += png.Length;

                if (jobs == 1 || mismatch)
                {
                    Diag.Trace("[更好的存档] 缩略图后台编码：首张 " + job.Width + "x" + job.Height
                        + "，" + png.Length + " 字节，后台耗时 " + ms + " ms（主线程抓像素 "
                        + lastMainMs + " ms）"
                        + (job.Reference != null ? "；同一张在主线程用原方法编码要 " + refEncodeMs + " ms —— 这就是本刀从主线程拿走的部分" : "")
                        + (verified ? "，行序=原序" : "") + (flip ? "，行序=翻转" : "")
                        + (job.Reference != null ? "，与主线程结果字节一致" : "")
                        + (lastNote.Length > 0 ? " ｜ " + lastNote : ""));
                }
            }
            catch (Exception e)
            {
                fallbacks++;
                Debug.LogError("[更好的存档] 缩略图后台写盘失败: " + job.Path + " : " + e.Message);
            }
            finally
            {
                job.Pixels = null;
                job.Reference = null;
            }
        }

        private static byte[] Encode(ThumbJob job, bool flip)
        {
            int w = job.Width, h = job.Height;
            byte[] buf = new byte[w * h * 4];
            Color32[] px = job.Pixels;
            for (int y = 0; y < h; y++)
            {
                int srcRow = (flip ? (h - 1 - y) : y) * w;
                int di = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    Color32 c = px[srcRow + x];
                    buf[di] = c.r;
                    buf[di + 1] = c.g;
                    buf[di + 2] = c.b;
                    buf[di + 3] = c.a;
                    di += 4;
                }
            }
            return ImageConversion.EncodeArrayToPNG(buf, GraphicsFormat.R8G8B8A8_UNorm, (uint)w, (uint)h, (uint)(w * 4));
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        private static double Now()
        {
            return System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        internal static void Flush(int timeoutMs)
        {
            if (worker == null) return;
            int waited = 0;
            while (waited < timeoutMs)
            {
                bool busy;
                lock (Gate) busy = Queue.Count > 0;
                if (!busy) return;
                Thread.Sleep(50);
                waited += 50;
            }
        }

        internal static void Stop()
        {
            stopping = true;
            Signal.Set();
        }

        internal static string Summary()
        {
            if (!enabled && jobs == 0)
            {
                return "[更好的存档] 缩略图后台化：未启用";
            }
            return "[更好的存档] 缩略图后台化" + (enabled ? "" : "（已停用）") + "：后台编码 " + jobs
                + " 张，累计 " + totalEncodeMs + " ms，落盘 " + (bytes / 1048576.0).ToString("F2")
                + " MB，最近一张 " + lastEncodeMs + " ms；主线程只做像素抓取 " + lastMainMs
                + " ms；回退 " + fallbacks + " 次"
                + (mismatch ? " ｜ ★★ 曾出现字节不一致" : " ｜ 首张已与主线程结果逐字节比对通过")
                + " ｜ AsyncGPUReadback 请求 " + asyncRequests + " 次，完成 " + asyncCompleted
                + " 次，失败 " + asyncFailed + " 次"
                + " ｜ 预览 RT 复用 " + rtReuses + " 次，重建 " + rtRebuilds + " 次"
                + " ｜ 预览图：真实捕获 " + previewCaptures + " 次，自动跳过 " + previewSkips + " 次";
        }
    }
}