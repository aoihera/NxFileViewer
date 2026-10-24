using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Emignatik.NxFileViewer.FileLoading;
using Emignatik.NxFileViewer.Localization;
using Emignatik.NxFileViewer.Models.TreeItems;
using Emignatik.NxFileViewer.Models.TreeItems.Impl;
using Emignatik.NxFileViewer.Tools;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using Microsoft.Extensions.Logging;
using Path = System.IO.Path;

namespace Emignatik.NxFileViewer.Services.BackgroundTask.RunnableImpl;

/// <summary>
/// Dumps the whole content of a partition (NSP/NSZ, or a XCI/XCZ partition) in a nxdumptool-like layout:
///
/// <code>
/// &lt;target directory&gt;/&lt;package name&gt;/
///     &lt;ticket&gt;.tik
///     &lt;certificate&gt;.cert
///     control/
///         control.nacp
///         icon_AmericanEnglish.dat
///         ...
///     exefs/
///         main
///         main.npdm
///         ...
///     legal_information/
///         ...
///     logo/
///         ...
///     meta/
///         Application_0100ABCD12345678.cnmt
///         ...
///     romfs/
///         ...
/// </code>
///
/// Every NCA section is dumped in a flat directory named after what it contains:
/// the Program NCA sections give «exefs» (code), «romfs» (data) and «logo» (logo), the Control NCA gives «control»,
/// the Meta NCA gives «meta» and the Manual NCA gives «legal_information». Any other content type or section
/// falls back to a lower_snake_case name derived from the content type (e.g. «data», «public_data») or to
/// «section_&lt;index&gt;».
/// </summary>
public class DumpPackageContentRunnable : IDumpPackageContentRunnable
{
    private const string ROOT_PATH = "/";

    private const string EXEFS_DIRECTORY_NAME = "exefs";
    private const string ROMFS_DIRECTORY_NAME = "romfs";
    private const string LOGO_DIRECTORY_NAME = "logo";
    private const string CONTROL_DIRECTORY_NAME = "control";
    private const string LEGAL_INFORMATION_DIRECTORY_NAME = "legal_information";
    private const string META_DIRECTORY_NAME = "meta";

    private readonly IStreamToFileHelper _streamToFileHelper;
    private readonly IFsSanitizer _fsSanitizer;
    private readonly IFileItemLoader _fileItemLoader;
    private readonly IPackageTypeAnalyzer _packageTypeAnalyzer;
    private readonly ILogger _logger;

    private PartitionFileSystemItemBase? _partitionItem;
    private string? _targetDirectory;
    private string? _basePackageFilePath;

    private bool _basePackageLoadingAttempted;
    private IItem? _basePackageItem;

    /// <summary>
    /// File systems built by <see cref="TryOpenPatchedSectionFileSystem"/>, which are not owned by any item and must be disposed.
    /// </summary>
    private readonly List<IFileSystem> _ownedFileSystems = new();

    public DumpPackageContentRunnable(IStreamToFileHelper streamToFileHelper, IFsSanitizer fsSanitizer, IFileItemLoader fileItemLoader, IPackageTypeAnalyzer packageTypeAnalyzer, ILoggerFactory loggerFactory)
    {
        _streamToFileHelper = streamToFileHelper ?? throw new ArgumentNullException(nameof(streamToFileHelper));
        _fsSanitizer = fsSanitizer ?? throw new ArgumentNullException(nameof(fsSanitizer));
        _fileItemLoader = fileItemLoader ?? throw new ArgumentNullException(nameof(fileItemLoader));
        _packageTypeAnalyzer = packageTypeAnalyzer ?? throw new ArgumentNullException(nameof(packageTypeAnalyzer));
        _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory))).CreateLogger(this.GetType());
    }

    public bool SupportsCancellation => true;

    public bool SupportProgress => true;

    public void Setup(PartitionFileSystemItemBase partitionItem, string targetDirectory, string? basePackageFilePath = null)
    {
        _partitionItem = partitionItem ?? throw new ArgumentNullException(nameof(partitionItem));
        _targetDirectory = targetDirectory ?? throw new ArgumentNullException(nameof(targetDirectory));
        _basePackageFilePath = basePackageFilePath;
        _basePackageLoadingAttempted = false;
        _basePackageItem = null;
        _ownedFileSystems.Clear();
    }

    public void Run(IProgressReporter progressReporter, CancellationToken cancellationToken)
    {
        if (_partitionItem == null || _targetDirectory == null)
            throw new InvalidOperationException($"{nameof(Setup)} should be called first.");

        var dumpRootDirectory = GetDumpRootDirectory(_partitionItem, _targetDirectory);

        try
        {
            if (!Directory.Exists(dumpRootDirectory))
                Directory.CreateDirectory(dumpRootDirectory);

            // Builds the whole list of files to dump, and creates all the target directories on the fly
            var filesToDump = new List<PlannedFile>();

            PlanPartitionRootFiles(_partitionItem, dumpRootDirectory, filesToDump);
            PlanNcaContent(_partitionItem, dumpRootDirectory, filesToDump);

            var nbFiles = filesToDump.Count;
            var fileNum = 0;

            foreach (var plannedFile in filesToDump)
            {
                cancellationToken.ThrowIfCancellationRequested();

                fileNum++;

                var relativeFilePath = Path.GetRelativePath(dumpRootDirectory, plannedFile.DstFilePath);
                var progressText = LocalizationManager.Instance.Current.Keys.Status_DumpingContent.SafeFormat(relativeFilePath, fileNum, nbFiles);
                progressReporter.SetText(progressText);
                progressReporter.SetPercentage(nbFiles == 0 ? 1.0 : (double)fileNum / nbFiles);

                DumpFile(plannedFile, cancellationToken);
            }

            progressReporter.SetPercentage(1.0);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(LocalizationManager.Instance.Current.Keys.Log_DumpContentCanceled);
        }
        finally
        {
            foreach (var ownedFileSystem in _ownedFileSystems)
            {
                try
                {
                    ownedFileSystem.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to dispose a file system built from a base NCA.");
                }
            }

            _ownedFileSystems.Clear();

            _basePackageItem?.Dispose();
            _basePackageItem = null;
        }
    }

    private void DumpFile(PlannedFile plannedFile, CancellationToken cancellationToken)
    {
        try
        {
            if (plannedFile.SourceFileSystem != null && plannedFile.SourcePath != null)
            {
                using var uniqueRefFile = new UniqueRef<IFile>();
                plannedFile.SourceFileSystem.OpenFile(ref uniqueRefFile.Ref, plannedFile.SourcePath.ToU8Span(), OpenMode.Read).ThrowIfFailure();
                using var file = uniqueRefFile.Release();
                _streamToFileHelper.Save(file.AsStream(), plannedFile.DstFilePath, cancellationToken);
            }
            else if (plannedFile.PartitionFileItem != null)
            {
                using var file = plannedFile.PartitionFileItem.LoadFile();
                _streamToFileHelper.Save(file.AsStream(), plannedFile.DstFilePath, cancellationToken);
            }
            else
            {
                throw new InvalidOperationException($"Nothing to dump for file «{plannedFile.DstFilePath}».");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LocalizationManager.Instance.Current.Keys.DumpContent_FileError.SafeFormat(plannedFile.DstFilePath, ex.Message));
        }
    }

    /// <summary>
    /// Saves the files stored at the root of the partition (tickets, certificates, ...) directly into the dump root directory.
    /// </summary>
    private void PlanPartitionRootFiles(PartitionFileSystemItemBase partitionItem, string dumpRootDirectory, List<PlannedFile> filesToDump)
    {
        foreach (var partitionFileItem in partitionItem.PartitionFileEntryChildItems)
        {
            if (partitionFileItem is NcaItem) // NCAs are dumped through their sections content
                continue;

            var dstFilePath = Path.Combine(dumpRootDirectory, _fsSanitizer.SanitizeFileName(partitionFileItem.Name));

            filesToDump.Add(PlannedFile.FromPartitionFileItem(partitionFileItem, dstFilePath));
        }
    }

    /// <summary>
    /// Plans the content of every NCA section, each one being dumped in a directory of the dump root named after
    /// what the section contains: «exefs», «romfs» and «logo» for the Program NCA, «control» for the Control NCA,
    /// «meta» for the Meta NCA and «legal_information» for the Manual NCA.
    /// </summary>
    private void PlanNcaContent(PartitionFileSystemItemBase partitionItem, string dumpRootDirectory, List<PlannedFile> filesToDump)
    {
        foreach (var ncaItem in partitionItem.NcaChildItems)
        {
            var sections = ncaItem.ChildItems;
            if (sections.Length == 0)
            {
                _logger.LogWarning(LocalizationManager.Instance.Current.Keys.DumpContent_NcaWithoutSection.SafeFormat(ncaItem.Name));
                continue;
            }

            var contentDirectoryName = GetContentDirectoryName(ncaItem);

            for (var sectionIndex = 0; sectionIndex < sections.Length; sectionIndex++)
            {
                var sectionItem = sections[sectionIndex];

                // The Program NCA holds several sections (code, data, logo): each one is dumped in its own directory.
                // Any other content type holds a single section, dumped in the content type directory itself.
                var directoryName = ncaItem.ContentType == NcaContentType.Program
                    ? GetProgramSectionDirectoryName(sectionItem)
                    : (sectionIndex == 0 ? contentDirectoryName : $"{contentDirectoryName}_{sectionItem.SectionIndex}");

                var sectionDirectory = Path.Combine(dumpRootDirectory, directoryName);

                IFileSystem? fileSystem = sectionItem.FileSystem;

                if (fileSystem == null && sectionItem.SectionType.HasFlag(SectionType.Patch))
                {
                    // A patch (BKTR) section only holds the data modified by an update: it can only be read by combining
                    // it with its base NCA, which is either part of the same package or of a separate base package.
                    fileSystem = TryOpenPatchedSectionFileSystem(partitionItem, ncaItem, sectionItem, out var baseNcaName);
                    if (fileSystem == null)
                        _logger.LogWarning(LocalizationManager.Instance.Current.Keys.DumpContent_PatchSectionSkipped.SafeFormat(ncaItem.Name, sectionItem.SectionIndex));
                    else
                        _logger.LogInformation(LocalizationManager.Instance.Current.Keys.DumpContent_BaseNcaUsed.SafeFormat(ncaItem.Name, sectionItem.SectionIndex, baseNcaName));
                }

                if (fileSystem == null)
                {
                    if (!sectionItem.SectionType.HasFlag(SectionType.Patch))
                        _logger.LogWarning(LocalizationManager.Instance.Current.Keys.DumpContent_SectionNotOpened.SafeFormat(ncaItem.Name, sectionItem.SectionIndex));
                    continue;
                }

                try
                {
                    if (!Directory.Exists(sectionDirectory))
                        Directory.CreateDirectory(sectionDirectory);

                    PlanDirectory(fileSystem, ROOT_PATH, sectionDirectory, filesToDump);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, LocalizationManager.Instance.Current.Keys.DumpContent_SectionError.SafeFormat(ncaItem.Name, sectionItem.SectionIndex, ex.Message));
                }
            }
        }
    }

    /// <summary>
    /// Gets the name of the directory holding the content of the specified NCA, based on its content type:
    /// «control» for the Control NCA, «meta» for the Meta NCA, «legal_information» for the Manual NCA, etc.
    /// </summary>
    private static string GetContentDirectoryName(NcaItem ncaItem)
    {
        switch (ncaItem.ContentType)
        {
            case NcaContentType.Control:
                return CONTROL_DIRECTORY_NAME;

            case NcaContentType.Meta:
                return META_DIRECTORY_NAME;

            case NcaContentType.Manual:
                return LEGAL_INFORMATION_DIRECTORY_NAME;

            default:
                // e.g. NcaContentType.Data => «data», NcaContentType.PublicData => «public_data»
                return ToSnakeCase(ncaItem.ContentType.ToString());
        }
    }

    /// <summary>
    /// Gets the name of the directory holding the specified Program NCA section: the code section holds the «exefs»
    /// content, the data section holds the «romfs» content and the logo section holds the «logo» content.
    /// </summary>
    private static string GetProgramSectionDirectoryName(SectionItem sectionItem)
    {
        switch (sectionItem.NcaSectionType)
        {
            case NcaSectionType.Code:
                return EXEFS_DIRECTORY_NAME;

            case NcaSectionType.Data:
                return ROMFS_DIRECTORY_NAME;

            case NcaSectionType.Logo:
                return LOGO_DIRECTORY_NAME;

            default:
                return $"section_{sectionItem.SectionIndex}";
        }
    }

    private static string ToSnakeCase(string value)
    {
        var snakeCase = new System.Text.StringBuilder(value.Length + 4);

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            if (char.IsUpper(c))
            {
                if (i > 0 && (!char.IsUpper(value[i - 1]) || (i + 1 < value.Length && !char.IsUpper(value[i + 1]))))
                    snakeCase.Append('_');

                snakeCase.Append(char.ToLowerInvariant(c));
            }
            else
            {
                snakeCase.Append(c);
            }
        }

        return snakeCase.ToString();
    }

    /// <summary>
    /// Tries to open the file system of a patch (BKTR) section by combining it with its base NCA.
    /// The base NCA is searched in the current package first (a package can hold both the base and the update content),
    /// then, if any, in the base package file selected by the user.
    /// </summary>
    /// <returns>The file system rebuilt from the base NCA and the patch section, or null if no usable base NCA was found.</returns>
    private IFileSystem? TryOpenPatchedSectionFileSystem(PartitionFileSystemItemBase partitionItem, NcaItem patchNcaItem, SectionItem sectionItem, out string? baseNcaName)
    {
        baseNcaName = null;

        var sectionIndex = sectionItem.SectionIndex;

        var baseNcaItems = new List<NcaItem>(partitionItem.NcaChildItems);

        var basePackageItem = GetOrLoadBasePackageItem();
        if (basePackageItem != null)
            baseNcaItems.AddRange(basePackageItem.FindChildrenOfType<NcaItem>(includeItem: true));

        foreach (var baseNcaItem in baseNcaItems)
        {
            if (ReferenceEquals(baseNcaItem, patchNcaItem))
                continue;

            // The base NCA holds the same content as the patch NCA (a Program patch section is applied on a Program NCA)
            if (baseNcaItem.ContentType != patchNcaItem.ContentType)
                continue;

            try
            {
                var baseNca = baseNcaItem.Nca;

                if (!baseNca.Header.IsSectionEnabled(sectionIndex))
                    continue;

                var baseFsHeader = baseNca.GetFsHeader(sectionIndex);

                if (baseFsHeader.IsPatchSection())
                    continue; // This candidate is itself a patch NCA

                if (baseFsHeader.ExistsSparseLayer())
                    continue; // Sparse NCAs are update content too, they can't act as a base

                var fileSystem = baseNca.OpenFileSystemWithPatch(patchNcaItem.Nca, sectionIndex, IntegrityCheckLevel.IgnoreOnInvalid);
                _ownedFileSystems.Add(fileSystem);

                baseNcaName = baseNcaItem.Name;
                return fileSystem;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "NCA «{0}» can't be used as a base for section {1} of NCA «{2}».",
                    baseNcaItem.Name, sectionIndex, patchNcaItem.Name);
            }
        }

        return null;
    }

    /// <summary>
    /// Loads the base package file selected by the user, once per dump.
    /// </summary>
    private IItem? GetOrLoadBasePackageItem()
    {
        if (_basePackageLoadingAttempted)
            return _basePackageItem;

        _basePackageLoadingAttempted = true;

        var basePackageFilePath = _basePackageFilePath;
        if (string.IsNullOrWhiteSpace(basePackageFilePath))
            return null;

        try
        {
            switch (_packageTypeAnalyzer.GetType(basePackageFilePath))
            {
                case PackageType.NSP:
                    _basePackageItem = _fileItemLoader.LoadNsp(basePackageFilePath);
                    break;

                case PackageType.XCI:
                    _basePackageItem = _fileItemLoader.LoadXci(basePackageFilePath);
                    break;

                default:
                    _logger.LogWarning(LocalizationManager.Instance.Current.Keys.DumpContent_BasePackageUnsupported.SafeFormat(basePackageFilePath));
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LocalizationManager.Instance.Current.Keys.DumpContent_BasePackageError.SafeFormat(basePackageFilePath, ex.Message));
        }

        return _basePackageItem;
    }

    private void PlanDirectory(IFileSystem fileSystem, string srcDirectoryPath, string dstDirectoryPath, List<PlannedFile> filesToDump)
    {
        foreach (var directoryEntry in fileSystem.EnumerateEntries(srcDirectoryPath, "*", SearchOptions.Default))
        {
            var dstPath = Path.Combine(dstDirectoryPath, _fsSanitizer.SanitizeFileName(directoryEntry.Name));

            if (directoryEntry.Type == DirectoryEntryType.Directory)
            {
                if (!Directory.Exists(dstPath))
                    Directory.CreateDirectory(dstPath);

                PlanDirectory(fileSystem, directoryEntry.FullPath, dstPath, filesToDump);
            }
            else
            {
                filesToDump.Add(PlannedFile.FromFileSystemEntry(fileSystem, directoryEntry.FullPath, dstPath));
            }
        }
    }

    /// <summary>
    /// Gets the dump root directory, which is a sub-directory of the target directory named after the package.
    /// For a XCI partition, an additional directory level is added for the partition itself.
    /// </summary>
    private string GetDumpRootDirectory(PartitionFileSystemItemBase partitionItem, string targetDirectory)
    {
        var packageName = partitionItem.ParentItem is XciItem xciItem ? xciItem.Name : partitionItem.Name;
        var packageDirectoryName = _fsSanitizer.SanitizeFileName(Path.GetFileNameWithoutExtension(packageName));

        var dumpRootDirectoryName = partitionItem.ParentItem is XciItem
            ? Path.Combine(packageDirectoryName, _fsSanitizer.SanitizeFileName(partitionItem.Name))
            : packageDirectoryName;

        return Path.Combine(targetDirectory, dumpRootDirectoryName);
    }

    private class PlannedFile
    {
        private PlannedFile(string dstFilePath, IFileSystem? sourceFileSystem, string? sourcePath, PartitionFileEntryItemBase? partitionFileItem)
        {
            DstFilePath = dstFilePath;
            SourceFileSystem = sourceFileSystem;
            SourcePath = sourcePath;
            PartitionFileItem = partitionFileItem;
        }

        public static PlannedFile FromFileSystemEntry(IFileSystem sourceFileSystem, string sourcePath, string dstFilePath)
        {
            return new PlannedFile(dstFilePath, sourceFileSystem, sourcePath, null);
        }

        public static PlannedFile FromPartitionFileItem(PartitionFileEntryItemBase partitionFileItem, string dstFilePath)
        {
            return new PlannedFile(dstFilePath, null, null, partitionFileItem);
        }

        public string DstFilePath { get; }

        public IFileSystem? SourceFileSystem { get; }

        public string? SourcePath { get; }

        public PartitionFileEntryItemBase? PartitionFileItem { get; }
    }
}

public interface IDumpPackageContentRunnable : IRunnable
{
    /// <summary>
    /// </summary>
    /// <param name="partitionItem">The partition to dump</param>
    /// <param name="targetDirectory">The directory where the dump directory is created</param>
    /// <param name="basePackageFilePath">Optional path of the base package, required to dump patch (BKTR) sections</param>
    void Setup(PartitionFileSystemItemBase partitionItem, string targetDirectory, string? basePackageFilePath = null);
}
