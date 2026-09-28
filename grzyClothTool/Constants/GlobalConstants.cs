using grzyClothTool.Helpers;
using System;

namespace grzyClothTool.Constants;

public static class GlobalConstants
{
    // Highest drawable number the game supports; numbering is 0-based (000-255).
    public const int MAX_DRAWABLE_NUMBER_LIMIT = 255;

    // Capacity of one addon. The user setting is the highest allowed drawable number,
    // so the count is that number + 1 (setting 255 -> numbers 000-255 -> 256 drawables).
    public static int MAX_DRAWABLES_IN_ADDON => SettingsHelper.Instance.MaxDrawableNumber + 1;

    public const int MAX_DRAWABLE_TEXTURES = 26;
    public const string ASSETS_FOLDER_NAME = "project_assets";
    public static readonly Uri DISCORD_INVITE_URL = new("https://heyyczer.com/discord");
    public static readonly string GRZY_TOOLS_URL = "https://heyyczer.com/grzyClothTool";
    // public static readonly string GRZY_TOOLS_URL = "https://grzy.tools";
}
