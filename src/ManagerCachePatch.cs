using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;

namespace SaveOpt
{
    internal static class ManagerCachePatch
    {
        // ─── 模式开关 ───
        // false = 保守版：只保留 typeInfoMap 跨存档缓存，其他 6 项照原样清空。
        //                 存档体积与原版一致；省掉所有 EncodeTypeInfo 反射。
        // true  = 激进版：额外保留 serializationTemplatesByTypeName / serializationTemplatesByType。
        //                 存档目录会包含历史见过的所有类型（体积略大），但下次存档省掉 SerializationTemplate 重建。
        private const bool Aggressive = false;

        internal static bool Apply(Harmony harmony)
        {
            MethodInfo clearMethod = AccessTools.Method(typeof(KSerialization.Manager), "Clear", null, null);
            if (clearMethod == null)
            {
                Debug.LogWarning("[更好的存档] 找不到 KSerialization.Manager.Clear，缓存加速未挂载");
                return false;
            }

            serTemplatesByTypeName = AccessTools.Field(typeof(KSerialization.Manager), "serializationTemplatesByTypeName");
            serTemplatesByType = AccessTools.Field(typeof(KSerialization.Manager), "serializationTemplatesByType");
            deserTemplatesByTypeName = AccessTools.Field(typeof(KSerialization.Manager), "deserializationTemplatesByTypeName");
            deserTemplatesByType = AccessTools.Field(typeof(KSerialization.Manager), "deserializationTemplatesByType");
            deserMappings = AccessTools.Field(typeof(KSerialization.Manager), "deserializationMappings");

            if (serTemplatesByTypeName == null
                || serTemplatesByType == null
                || deserTemplatesByTypeName == null
                || deserTemplatesByType == null
                || deserMappings == null)
            {
                Debug.LogWarning("[更好的存档] Manager 私有字典字段解析失败，缓存加速未挂载");
                return false;
            }

            clearTypeInfoMask = AccessTools.Method(typeof(KSerialization.Helper), "ClearTypeInfoMask", null, null);
            if (clearTypeInfoMask == null)
            {
                Debug.LogWarning("[更好的存档] Helper.ClearTypeInfoMask 解析失败，缓存加速未挂载");
                return false;
            }

            try
            {
                harmony.Patch(clearMethod,
                    new HarmonyMethod(AccessTools.Method(typeof(ManagerCachePatch), "Clear_Prefix", null, null)),
                    null, null, null);
            }
            catch (Exception ex)
            {
                Debug.LogError("[更好的存档] Manager.Clear 补丁挂载失败: " + ex.Message);
                return false;
            }

            Diag.Trace("[更好的存档] KSerialization.Manager.Clear 已挂载（模式="
                + (Aggressive ? "激进" : "保守") + "）");
            return true;
        }

        // 原版 Clear 逐项清空 7 个缓存。本补丁按模式保留其中若干：
        //   保守：保留 typeInfoMap（纯反射缓存，跨存档有效）
        //   激进：额外保留 serializationTemplatesByTypeName / serializationTemplatesByType
        // 出错时直接返回 true 放行原版，保证安全。
        public static bool Clear_Prefix()
        {
            try
            {
                if (!Aggressive)
                {
                    ClearDict(serTemplatesByTypeName);
                    ClearDict(serTemplatesByType);
                }
                // Aggressive 下不清 serTemplates 两个字典
                ClearDict(deserTemplatesByTypeName);
                ClearDict(deserTemplatesByType);
                ClearDict(deserMappings);
                // Manager.typeInfoMap 两种模式下都保留
                clearTypeInfoMask.Invoke(null, null);
                skipped++;
            }
            catch (Exception ex)
            {
                lastError = ex.GetType().Name + " " + ex.Message;
                // 出错就让原版跑，保证安全
                return true;
            }
            return false;
        }

        private static void ClearDict(FieldInfo field)
        {
            IDictionary dict = field.GetValue(null) as IDictionary;
            if (dict != null)
            {
                dict.Clear();
            }
        }

        internal static string Summary()
        {
            return "[更好的存档] Manager.Clear 加速（" + (Aggressive ? "激进" : "保守")
                + "）：跳过 " + skipped + " 次清空"
                + (lastError.Length > 0 ? "，错误=" + lastError : "");
        }

        private static FieldInfo serTemplatesByTypeName;
        private static FieldInfo serTemplatesByType;
        private static FieldInfo deserTemplatesByTypeName;
        private static FieldInfo deserTemplatesByType;
        private static FieldInfo deserMappings;
        private static MethodInfo clearTypeInfoMask;
        private static long skipped;
        private static string lastError = "";
    }
}