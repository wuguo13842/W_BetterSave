using System;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace SaveOpt
{
    internal static class LoadColonyPreviewPatch
    {
        internal static bool Apply(HarmonyLib.Harmony harmony)
        {
            MethodInfo target = AccessTools.Method(typeof(RetireColonyUtility), "LoadColonyPreview");
            if (target == null)
            {
                Debug.LogWarning("[更好的存档] 找不到 RetireColonyUtility.LoadColonyPreview，预览图重定向未挂载");
                return false;
            }

            try
            {
                harmony.Patch(target,
                    prefix: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(LoadColonyPreviewPatch), "Prefix")));
            }
            catch (Exception e)
            {
                Debug.LogError("[更好的存档] LoadColonyPreview 补丁挂载失败: " + e.Message);
                return false;
            }

            Debug.Log("[更好的存档] LoadColonyPreview 已挂载（自动存档预览图运行时重定向到最近一次手动存档）");
            return true;
        }

        // 重定向逻辑：
        //   1. 只处理自动存档，且该自动存档自己的 png 不存在
        //   2. 优先用本会话记录的手动存档（快速路径）
        //   3. 否则扫描"自动存档所属殖民地文件夹"，找最新一张有 png 的手动存档
        // 找到后把 __0 换成手动存档的 .sav 路径，
        // 后续 Path.ChangeExtension(__0, ".png") 自然指向手动存档的 png。
        //
        // ★★ 关键：参数名必须是 __0 —— HarmonyLib 用它按位置匹配原方法的第 0 个参数
        //   （原方法签名：LoadColonyPreview(string savePath, string colonyName, bool fallbackToTimelapse)）
        //   用 ref 修饰才能把新值写回原方法。用普通名字（如 savePath）HarmonyLib 不认识，不会写回。
        public static void Prefix(ref string __0)
        {
            if (string.IsNullOrEmpty(__0)) return;

            // 非自动存档，不重定向
            if (!SaveLoader.IsSaveAuto(__0)) return;

            // 自动存档自己的 png 存在（AutoSaveThumbnail=true 时会有），不重定向
            try
            {
                if (File.Exists(Path.ChangeExtension(__0, ".png"))) return;
            }
            catch (Exception) { return; }

            // 快速路径：本会话手动存过档
            string lastManual = ThumbnailAsync.GetLastManualSavePath();
            if (!string.IsNullOrEmpty(lastManual) && HasPng(lastManual))
            {
                Diag.Trace("[更好的存档] 自动存档预览图重定向（会话记录）-> " + Path.GetFileName(lastManual));
                __0 = lastManual;
                return;
            }

            // 扫描路径：从自动存档路径推导殖民地文件夹，找最新一张有 png 的手动存档
            try
            {
                string autoSaveDir = Path.GetDirectoryName(__0);
                if (string.IsNullOrEmpty(autoSaveDir)) return;
                string colonyDir = Path.GetDirectoryName(autoSaveDir);
                if (string.IsNullOrEmpty(colonyDir) || !Directory.Exists(colonyDir)) return;

                string[] savFiles = Directory.GetFiles(colonyDir, "*.sav", SearchOption.TopDirectoryOnly);
                if (savFiles == null || savFiles.Length == 0) return;

                // 按最后修改时间降序（最新在前）
                Array.Sort(savFiles, delegate (string a, string b)
                {
                    try { return File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)); }
                    catch (Exception) { return 0; }
                });

                for (int i = 0; i < savFiles.Length; i++)
                {
                    string sav = savFiles[i];
                    if (Path.GetFileName(sav).StartsWith("._")) continue;
                    if (!HasPng(sav)) continue;

                    Diag.Trace("[更好的存档] 自动存档预览图重定向（扫描）-> " + Path.GetFileName(sav));
                    __0 = sav;
                    return;
                }
            }
            catch (Exception e)
            {
                Diag.Trace("[更好的存档] 预览图重定向扫描失败: " + e.Message);
            }
        }

        private static bool HasPng(string savPath)
        {
            try
            {
                string png = Path.ChangeExtension(savPath, ".png");
                return !string.IsNullOrEmpty(png) && File.Exists(png);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}