namespace DiskSweeper.App.Infrastructure;

/// <summary>Named glyphs from the Segoe Fluent Icons font (the icon font Windows 11 itself uses).</summary>
public static class Icons
{
    public static readonly string Home = Glyph(0xE80F);
    public static readonly string Folder = Glyph(0xE8B7);
    public static readonly string File = Glyph(0xE7C3);
    public static readonly string Link = Glyph(0xE71B);
    public static readonly string Tiles = Glyph(0xECA5);
    public static readonly string Pie = Glyph(0xEB05);
    public static readonly string Broom = Glyph(0xEA99);
    public static readonly string Trending = Glyph(0xE9D2);
    public static readonly string History = Glyph(0xE81C);
    public static readonly string Settings = Glyph(0xE713);
    public static readonly string Check = Glyph(0xE73E);
    public static readonly string Cancel = Glyph(0xE711);
    public static readonly string Warning = Glyph(0xE7BA);
    public static readonly string Clock = Glyph(0xE823);
    public static readonly string Info = Glyph(0xE946);
    public static readonly string Game = Glyph(0xE7FC);
    public static readonly string Package = Glyph(0xE7B8);
    public static readonly string Download = Glyph(0xE896);
    public static readonly string Code = Glyph(0xE943);
    public static readonly string Save = Glyph(0xE74E);
    public static readonly string Lock = Glyph(0xE72E);
    public static readonly string Cloud = Glyph(0xE753);

    private static string Glyph(int code) => char.ConvertFromUtf32(code);
}
