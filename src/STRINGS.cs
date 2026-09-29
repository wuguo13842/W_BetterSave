namespace SaveOpt
{
	public static class STRINGS
	{
		public static class BETTERSAVE
		{
			public static class OPTIONS
			{
				public static class CATEGORIES
				{
					public static LocString GC = "GC (Requires Restart)";
					public static LocString THUMBNAIL = "Thumbnail";
					public static LocString SAVE = "System Save Logic";
				}
				
				public static class MANUALGCMODE
				{
					public static LocString NAME = "Manual GC Management (Advanced)";
					public static LocString TOOLTIP = "Auto (recommended): GCMode=Enabled, the game manages GC on its own; recommended for low-memory users. Manual (advanced): GCMode=Disabled, the mod's GcModeGate controls collection timing; may reduce in-game stutter on high-memory systems only. Restart required.";
				}

				public static class GCMAXTIMESLICE
				{
					public static LocString NAME = "gc-max-time-slice";
					public static LocString TOOLTIP = "Writes gc-max-time-slice into OxygenNotIncluded_Data/boot.config. Range 1-6, default 3. Restart required.";
				}

				public static class AUTOSAVETHUMBNAIL
				{
					public static LocString NAME = "Thumbnail on autosave too";
					public static LocString TOOLTIP = "Off by default. Autosaves are temporary; thumbnails add little value. Manual saves always capture a thumbnail.";
				}
				
                public static class MANUALSAVEALLOWGC
                {
                    public static LocString NAME = "Allow GC on manual save";
                    public static LocString TOOLTIP = "On by default. Manual saves run a full GC at the end (vanilla behaviour). Turn off to skip it — save is faster but heap peak is higher. Restart not required.";
                }
			}
		}
	}
}