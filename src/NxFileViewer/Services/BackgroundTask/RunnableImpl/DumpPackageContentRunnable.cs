using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Emignatik.NxFileViewer.Localization;
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
///     Program/
///         exefs/
///             ...
///         romfs/
///             ...
///     Control/
///         romfs/
///             control/
///                 ...
///     Meta/
///         cnmt/
///             ...
///     Manual/
///         romfs/
///             ...
/// </code>
///
/// Each NCA gets a directory named after its content type (Program, Control, Meta, Manual, ...), and each enabled
/// NCA section of that NCA gets a directory named after the section content (exefs, romfs, logo, ...).
/// </summary>
public class DumpPackageContentRunnable : IDumpPackageContentRunnable
{
    private const string ROOT_PATH = "/";

    private const string EXEFS_DIRECTORY_NAME = "exefs";
    private const string ROMFS_DIRECTORY_NAME = "romfs";
    private const string LOGO_DIRECTORY_NAME = "logo";
    private const string CNMT_DIRECTORY_NAME = "cnmt";

    private readonly IStreamToFileHelper _streamToFileHelper;
    private readonly IFsSanitizer _fsSanitizer;
    private readonly ILogger _logger;

    private PartitionFileSystemItemBase? _partitionItem;
    private string? _targetDirectory;

    public DumpPackageContentRunnable(IStreamToFileHelper streamToFileHelper, IFsSanitizer fsSanitizer, ILoggerFactory loggerFactory)
    {
        _streamToFileHelper = streamToFileHelper ?? throw new ArgumentNullException(nameof(streamToFileHelper));
        _fsSanitizer = fsSanitizer ?? throw new ArgumentNullException(nameof(fsSanitizer));
        _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory))).CreateLogger(this.GetType());
    }

    public bool SupportsCancellation => true;

    public bool SupportProgress => true;

    public void Setup(PartitionFileSystemItemBase partitionItem, string targetDirectory)
    {
        _partitionItem = partitionItem ?? throw new ArgumentNullException(nameof(partitionItem));
        _targetDirectory = targetDirectory ?? throw new ArgumentNullException(nameof(targetDirectory));
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
    /// Creates one directory per NCA (named after its content type), then one directory per section of that NCA
    /// (named after the section content: exefs, romfs, logo, ...), then plans every file of those sections.
    /// </summary>
    private void PlanNcaContent(PartitionFileSystemItemBase partitionItem, string dumpRootDirectory, List<PlannedFile> filesToDump)
    {
        var usedNcaDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ncaItem in partitionItem.NcaChildItems)
        {
            var sections = ncaItem.ChildItems;
            if (sections.Length == 0)
            {
                _logger.LogWarning(LocalizationManager.Instance.Current.Keys.DumpContent_NcaWithoutSection.SafeFormat(ncaItem.Name));
                continue;
            }

            var ncaDirectoryName = GetUniqueDirectoryName(GetNcaDirectoryName(ncaItem), GetShortId(ncaItem), usedNcaDirectoryNames);
            var ncaDirectory = Path.Combine(dumpRootDirectory, ncaDirectoryName);

            var usedSectionDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var sectionItem in sections)
            {
                var sectionDirectoryName = GetUniqueDirectoryName(GetSectionDirectoryName(sectionItem, ncaItem), $"section_{sectionItem.SectionIndex}", usedSectionDirectoryNames);
                var sectionDirectory = Path.Combine(ncaDirectory, sectionDirectoryName);

                var fileSystem = sectionItem.FileSystem;
                if (fileSystem == null)
                {
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
    /// Gets the name of the directory dedicated to the specified NCA, based on its content type
    /// (Program, Control, Meta, Manual, Data, ...).
    /// </summary>
    private static string GetNcaDirectoryName(NcaItem ncaItem)
    {
        var contentType = ncaItem.ContentType;
        return Enum.IsDefined(typeof(NcaContentType), contentType)
            ? contentType.ToString()
            : GetShortId(ncaItem);
    }

    /// <summary>
    /// Gets the name of the directory dedicated to the specified NCA section, based on the section content:
    /// a code section holds the «exefs» content, a data section holds the «romfs» content.
    /// </summary>
    private static string GetSectionDirectoryName(SectionItem sectionItem, NcaItem ncaItem)
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
                // The meta NCA section holds the CNMT metadata file
                return ncaItem.ContentType == NcaContentType.Meta
                    ? CNMT_DIRECTORY_NAME
                    : $"section_{sectionItem.SectionIndex}";
        }
    }

    private static string GetShortId(NcaItem ncaItem)
    {
        var id = ncaItem.Id;
        return id.Length > 8 ? id[..8] : id;
    }

    private static string GetUniqueDirectoryName(string directoryName, string disambiguationSuffix, HashSet<string> usedDirectoryNames)
    {
        if (usedDirectoryNames.Add(directoryName))
            return directoryName;

        var uniqueDirectoryName = $"{directoryName}_{disambiguationSuffix}";
        var index = 2;
        while (!usedDirectoryNames.Add(uniqueDirectoryName))
            uniqueDirectoryName = $"{directoryName}_{disambiguationSuffix}_{index++}";

        return uniqueDirectoryName;
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
    void Setup(PartitionFileSystemItemBase partitionItem, string targetDirectory);
}
