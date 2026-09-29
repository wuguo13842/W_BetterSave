using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using PeterHan.PLib.Options;

namespace SaveOpt
{
	// gc-max-time-slice 下拉框枚举：1-6 六档
	public enum GcMaxTimeSliceType
	{
		[Option("1")]
		One = 1,
		[Option("2")]
		Two = 2,
		[Option("3")]
		Three = 3,
		[Option("4")]
		Four = 4,
		[Option("5")]
		Five = 5,
		[Option("6")]
		Six = 6
	}

	// 自动存档附加动作四档下拉框。
	// GC 和截图同时做会明显卡顿，所以拆成互斥档 + 一个"全开"高级档。
	public enum AutoSaveExtrasType
	{
		[Option("STRINGS.BETTERSAVE.OPTIONS.AUTOSAVEEXTRAS.NONE")]
		None = 0,
		[Option("STRINGS.BETTERSAVE.OPTIONS.AUTOSAVEEXTRAS.GCONLY")]
		GcOnly = 1,
		[Option("STRINGS.BETTERSAVE.OPTIONS.AUTOSAVEEXTRAS.THUMBONLY")]
		ThumbnailOnly = 2,
		[Option("STRINGS.BETTERSAVE.OPTIONS.AUTOSAVEEXTRAS.BOTH")]
		Both = 3,
	}

    // [RestartRequired] 让 PLib 在玩家点“好的”后弹“需要重启游戏”的提示
    [RestartRequired]
	[ModInfo(null, null, false)]
	[ConfigFile("config.json", true, false)]
	[JsonObject(MemberSerialization.OptOut)]
	public sealed class BetterSaveOptions : IOptions
	{
		// 默认关闭：GCMode=Enabled，游戏自管理 GC（推荐，低内存用户必选）。
		// 打开=手动（高级）：GCMode=Disabled，由 mod 的 GcModeGate 控制回收时机。改动需重启游戏生效。
		[Option("STRINGS.BETTERSAVE.OPTIONS.MANUALGCMODE.NAME", "STRINGS.BETTERSAVE.OPTIONS.MANUALGCMODE.TOOLTIP", "STRINGS.BETTERSAVE.OPTIONS.CATEGORIES.GC")]
		[JsonProperty]
		public bool ManualGcMode { get; set; } = false;

		// gc-max-time-slice，1-6，默认 3。改动需重启游戏生效。
		[Option("STRINGS.BETTERSAVE.OPTIONS.GCMAXTIMESLICE.NAME", "STRINGS.BETTERSAVE.OPTIONS.GCMAXTIMESLICE.TOOLTIP", "STRINGS.BETTERSAVE.OPTIONS.CATEGORIES.GC")]
		[JsonProperty]
		public GcMaxTimeSliceType GcMaxTimeSlice { get; set; } = GcMaxTimeSliceType.Three;
		
        // 手动存档是否放行 GC。默认 true（原版行为：存档末尾清一次堆）。
        // 关闭后手动存档也不 GC，堆峰值会略高，但存档更快。
        [Option("STRINGS.BETTERSAVE.OPTIONS.MANUALSAVEALLOWGC.NAME", "STRINGS.BETTERSAVE.OPTIONS.MANUALSAVEALLOWGC.TOOLTIP", "STRINGS.BETTERSAVE.OPTIONS.CATEGORIES.SAVE")]
        [JsonProperty]
        public bool ManualSaveAllowGC { get; set; } = true;

		// 自动存档附加动作四档。GC 和截图同时做会明显卡顿，因此拆成互斥档 + 全开档。
		[Option("STRINGS.BETTERSAVE.OPTIONS.AUTOSAVEEXTRAS.NAME", "STRINGS.BETTERSAVE.OPTIONS.AUTOSAVEEXTRAS.TOOLTIP", "STRINGS.BETTERSAVE.OPTIONS.CATEGORIES.SAVE")]
		[JsonProperty]
		public AutoSaveExtrasType AutoSaveExtras { get; set; } = AutoSaveExtrasType.None;

		// PLib 在玩家点“好的”后调用此方法。
		// 这里立即把新值同步进内存字段，并写进 boot.config。
		public void OnOptionsChanged()
		{
			BetterSaveSettings.ApplyFromOptions(this);
			BootConfig.Apply((int)this.GcMaxTimeSlice);
		}

		// IOptions 接口要求，返回 null 表示使用默认选项界面。
		public IEnumerable<IOptionsEntry> CreateOptions()
		{
			return null;
		}
	}
}