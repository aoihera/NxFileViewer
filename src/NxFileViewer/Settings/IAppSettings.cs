using System.ComponentModel;
using Emignatik.NxFileViewer.Styling.Theme;
using Microsoft.Extensions.Logging;

namespace Emignatik.NxFileViewer.Settings;

/// <summary>
/// Exposes the list of application settings
/// </summary>
public interface IAppSettings : INotifyPropertyChanged
{

    string AppLanguage { get; set; }

    AppTheme Theme { get; set; }

    bool RememberWindowPlacement { get; set; }

    IWindowPlacementSettings MainWindowPlacement { get; }

    string LastUsedDir { get; set; }

    string LastOpenedFile { get; set; }

    string ProdKeysFilePath { get; set; }

    string ProdKeysDownloadUrl { get; set; }

    string TitleKeysFilePath { get; set; }

    string TitleKeysDownloadUrl { get; set; }

    bool OpenBlocklessCompressionNCZ { get; set; }

    bool IgnoreMissingDeltaFragments { get; set; }

    LogLevel LogLevel { get; set; }

    bool AlwaysReloadKeysBeforeOpen { get; set; }

    bool InjectTicketKeys { get; set; }

    string TitlePageUrl { get; set; }

    string TitleInfoApiUrl { get; set; }

    IRenamingOptions RenamingOptions { get; }

    int ProgressBufferSize { get; }
}

public interface IRenamingOptions : INotifyPropertyChanged
{
    bool AutoCloseOpenedFile { get; set; }

    string LastRenamePath { get; set; }

    string? FileFilters { get; set; }

    bool IncludeSubdirectories { get; set; }

    string ApplicationPattern { get; set; }

    string PatchPattern { get; set; }

    string AddonPattern { get; set; }

    bool IsSimulation { get; set; }

    string InvalidFileNameCharsReplacement { get; set; }

    bool ReplaceWhiteSpaceChars { get; set; }

    string WhiteSpaceCharsReplacement { get; set; }

}

/// <summary>
/// Position and size of the main window, in physical pixels
/// </summary>
public interface IWindowPlacementSettings : INotifyPropertyChanged
{
    int Left { get; set; }

    int Top { get; set; }

    int Width { get; set; }

    int Height { get; set; }

    bool IsMaximized { get; set; }

    /// <summary>True when a placement has already been saved</summary>
    bool IsDefined { get; }
}
