using DiskSweeper.Core.Deletion;

namespace DiskSweeper.Core.Tests;

public class ProtectedPathsTests
{
    private static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static readonly string Drive = Path.GetPathRoot(Windows)!;

    public static TheoryData<string> Refused => new()
    {
        Drive,
        "D:\\",
        Windows,
        Path.Join(Windows, @"System32\drivers"),
        Path.Join(Windows, "Temp"),
        Path.Join(Windows, "SoftwareDistribution"),
        Path.Join(Drive, "pagefile.sys"),
        Path.Join(Drive, "hiberfil.sys"),
        Path.Join(Drive, "System Volume Information"),
        Path.Join(Drive, @"$Recycle.Bin\S-1-5-21"),
        Path.GetDirectoryName(Profile)!,
        Profile,
        Path.Join(Profile, "Downloads"),
        Path.Join(Profile, "AppData"),
        Local,
        Path.Join(Local, @"Microsoft\Windows\UsrClass.dat"),
        ProgramFiles,
        Path.Join(ProgramFiles, @"WindowsApps\SomeApp_1.0"),
        Path.Join(ProgramFiles, "Windows Defender"),
        AppContext.BaseDirectory,
    };

    public static TheoryData<string> Allowed => new()
    {
        Path.Join(Windows, @"Temp\setup123.tmp"),
        Path.Join(Windows, @"SoftwareDistribution\Download\abc"),
        Path.Join(Profile, @"Downloads\old-installer.exe"),
        Path.Join(Local, "SomeUninstalledGame"),
        Path.Join(Local, @"NVIDIA\DXCache"),
        Path.Join(Local, @"Microsoft\Windows\INetCache\IE"),
        Path.Join(ProgramFiles, "Epic Games"),
        Path.Join(Path.GetTempPath(), "leftover.tmp"),
        Path.Join(Drive, "SomeOldGameFolder"),
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void Refuses_paths_Windows_or_you_need(string path)
    {
        Assert.NotNull(ProtectedPaths.WhyProtected(path));
    }

    [Theory]
    [MemberData(nameof(Allowed))]
    public void Allows_junk_and_leftovers(string path)
    {
        Assert.Null(ProtectedPaths.WhyProtected(path));
    }

    [Fact]
    public void Hibernation_file_explains_the_proper_way_to_remove_it()
    {
        Assert.Contains("powercfg /h off", ProtectedPaths.WhyProtected(Path.Join(Drive, "hiberfil.sys")));
    }

    [Fact]
    public void Warns_about_game_saves_and_program_folders()
    {
        Assert.Contains(PathWarnings.For(Path.Join(Profile, @"Saved Games\SomeGame")), w => w.Contains("game saves"));
        Assert.Contains(PathWarnings.For(Path.Join(ProgramFiles, "SomeApp")), w => w.Contains("uninstall"));
        Assert.Contains(PathWarnings.For(Path.Join(Profile, @"OneDrive\Documents\big.vmdk")), w => w.Contains("OneDrive"));
        Assert.Empty(PathWarnings.For(Path.Join(Drive, @"VMs\old.iso")));
    }
}
