using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.IO;
using System.Linq;
using Emignatik.NxFileViewer.FileLoading.Patching;
using Emignatik.NxFileViewer.Localization;
using Emignatik.NxFileViewer.Models.Overview;
using Emignatik.NxFileViewer.Models.TreeItems;
using Emignatik.NxFileViewer.Models.TreeItems.Impl;
using Emignatik.NxFileViewer.Services.KeysManagement;
using Emignatik.NxFileViewer.Settings;
using Emignatik.NxFileViewer.Utils.LibHacExtensions;
using LibHac;
using LibHac.Common;
using LibHac.Common.Keys;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Loader;
using LibHac.Ns;
using LibHac.NSZ;
using LibHac.Spl;
using LibHac.Tools.Es;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using LibHac.Tools.Ncm;
using Microsoft.Extensions.Logging;
using NcaFsHeader = LibHac.Tools.FsSystem.NcaUtils.NcaFsHeader;

namespace Emignatik.NxFileViewer.FileLoading;

public class FileItemLoader : IFileItemLoader
{
    /// <summary>
    /// Mask of the update (patch) bit of a title id: an update title id is «base title id | PATCH_TITLE_ID_MASK».
    /// </summary>
    internal const ulong PATCH_TITLE_ID_MASK = 0x800UL;

    private readonly IKeySetProviderService _keySetProviderService;
    private readonly IAppSettings _appSettings;
    private readonly IPackageTypeAnalyzer _packageTypeAnalyzer;
    private readonly IBasePackageFinder _basePackageFinder;
    private readonly ILogger _logger;

    public FileItemLoader(IKeySetProviderService keySetProviderService, ILoggerFactory loggerFactory, IAppSettings appSettings, IPackageTypeAnalyzer packageTypeAnalyzer, IBasePackageFinder basePackageFinder)
    {
        _keySetProviderService = keySetProviderService ?? throw new ArgumentNullException(nameof(keySetProviderService));
        _appSettings = appSettings ?? throw new ArgumentNullException(nameof(appSettings));
        _packageTypeAnalyzer = packageTypeAnalyzer ?? throw new ArgumentNullException(nameof(packageTypeAnalyzer));
        _basePackageFinder = basePackageFinder ?? throw new ArgumentNullException(nameof(basePackageFinder));
        _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory))).CreateLogger(this.GetType());
    }

    public event MissingKeyExceptionHandler? MissingKey;

    public NspItem LoadNsp(string nspFilePath)
    {
        var keySet = _keySetProviderService.GetKeySet(_appSettings.AlwaysReloadKeysBeforeOpen);
        var nspItem = NspItem.FromFile(nspFilePath, keySet);
        BuildPartitionChildItems(nspItem);
        ResolvePatchSections(nspItem, nspFilePath, keySet);
        return nspItem;
    }

    public XciItem LoadXci(string xciFilePath)
    {
        var keySet = _keySetProviderService.GetKeySet(_appSettings.AlwaysReloadKeysBeforeOpen);
        var xciItem = XciItem.FromFile(xciFilePath, keySet);
        BuildXciChildItems(xciItem);
        ResolvePatchSections(xciItem, xciFilePath, keySet);
        return xciItem;
    }

    private void BuildXciChildItems(XciItem parentItem)
    {
        var xci = parentItem.Xci;

        try
        {
            foreach (var xciPartitionType in Enum.GetValues<XciPartitionType>())
            {
                try
                {
                    if (!xci.HasPartition(xciPartitionType))
                        continue;
                }
                catch (Exception ex)
                {
                    OnLoadingException(ex, parentItem);
                    var message = LocalizationManager.Instance.Current.Keys.LoadingError_FailedToCheckIfXciPartitionExists.SafeFormat(ex.Message);
                    parentItem.Errors.Add(Category.Loading, message);
                    _logger.LogError(ex, message);
                    continue;
                }

                XciPartition xciPartition;
                try
                {
                    xciPartition = xci.OpenPartition(xciPartitionType);
                }
                catch (Exception ex)
                {
                    OnLoadingException(ex, parentItem);
                    var message = LocalizationManager.Instance.Current.Keys.LoadingError_FailedToOpenXciPartition.SafeFormat(ex.Message);
                    parentItem.Errors.Add(Category.Loading, message);
                    _logger.LogError(ex, message);
                    continue;
                }

                var xciPartitionItem = new XciPartitionItem(xciPartition, xciPartitionType, parentItem);
                BuildPartitionChildItems(xciPartitionItem);
            }
        }
        catch (Exception ex)
        {
            OnLoadingException(ex, parentItem);
            var message = LocalizationManager.Instance.Current.Keys.LoadingError_FailedToLoadXciContent.SafeFormat(ex.Message);
            parentItem.Errors.Add(Category.Loading, message);
            _logger.LogError(ex, message);
        }

    }

    private void BuildPartitionChildItems(PartitionFileSystemItemBase parentItem)
    {
        try
        {
            var partitionFileSystem = parentItem.PartitionFileSystem;

            var remainingEntries = new List<DirectoryEntryEx>();

            // First loop on *.tik files to inject title keys in KeySet
            foreach (var partitionFileEntry in partitionFileSystem.EnumerateEntries().Where(e => e.Type == DirectoryEntryType.File))
            {
                var fileName = partitionFileEntry.Name;
                if (!fileName.EndsWith(".tik", StringComparison.OrdinalIgnoreCase))
                {
                    remainingEntries.Add(partitionFileEntry);
                    continue;
                }

                IFile file;
                try
                {
                    file = partitionFileSystem.LoadFile(partitionFileEntry);
                }
                catch (Exception ex)
                {
                    OnLoadingException(ex, parentItem);
                    var partitionFileEntryItem = new PartitionFileEntryItem(partitionFileEntry, parentItem);
                    var message = LocalizationManager.Instance.Current.Keys.LoadingError_FailedToOpenPartitionFile.SafeFormat(ex.Message);
                    partitionFileEntryItem.Errors.Add(Category.Loading, message);
                    _logger.LogError(ex, message);
                    continue;
                }

                Ticket ticket;
                try
                {
                    // According to https://github.com/Thealexbarney/LibHac/pull/304, in order to avoid reading issues with Sha256PartitionFileSystem,
                    // we need to copy the whole ticket file to a MemoryStream before reading it (same related issue: https://github.com/Myster-Tee/NxFileViewer/issues/32)
                    using var ms = new MemoryStream();
                    file.AsStream().CopyTo(ms);
                    ms.Position = 0;

                    ticket = new Ticket(ms);
                }
                catch (Exception ex)
                {
                    OnLoadingException(ex, parentItem);
                    var partitionFileEntryItem = new PartitionFileEntryItem(partitionFileEntry, parentItem);
                    var message = LocalizationManager.Instance.Current.Keys.LoadingError_FailedToLoadTicketFile.SafeFormat(ex.Message);
                    partitionFileEntryItem.Errors.Add(Category.Loading, message);
                    _logger.LogError(ex, message);
                    continue;
                }

                var ticketItem = new TicketItem(ticket, partitionFileEntry, parentItem);

                if (_appSettings.InjectTicketKeys)
                {
                    try
                    {

                        var rightsId = new RightsId(ticket.RightsId);
                        var accessKey = new AccessKey(ticket.TitleKeyBlock);

                        ticketItem.RightsId = rightsId;
                        ticketItem.AccessKey = accessKey;

                        if (parentItem.KeySet.ExternalKeySet.Get(rightsId, out var existingAccessKey) == Result.Success)
                        {
                            // Here RightID key is already defined
                            if (existingAccessKey != accessKey)
                            {
                                // Replaces the RightID key with the one defined in the ticket
                                parentItem.KeySet.ExternalKeySet.Remove(rightsId);
                                parentItem.KeySet.ExternalKeySet.Add(rightsId, accessKey).ThrowIfFailure();
                                _logger.LogWarning(LocalizationManager.Instance.Current.Keys.LoadingWarning_TitleIdKeyReplaced.SafeFormat(rightsId.ToString(), accessKey.ToString(), fileName, existingAccessKey));
                            }
                            else
                            {
                                _logger.LogDebug(LocalizationManager.Instance.Current.Keys.LoadingDebug_TitleIdKeyAlreadyExists.SafeFormat(rightsId.ToString(), accessKey.ToString(), fileName));
                            }
                        }
                        else
                        {
                            parentItem.KeySet.ExternalKeySet.Add(rightsId, accessKey).ThrowIfFailure();
                            _logger.LogInformation(LocalizationManager.Instance.Current.Keys.LoadingInfo_TitleIdKeySuccessfullyInjected.SafeFormat(rightsId.ToString(), accessKey.ToString(), fileName));
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, LocalizationManager.Instance.Current.Keys.LoadingError_FailedToLoadTitleIdKey.SafeFormat(fileName, ex.Message));
                    }
                }
            }

            foreach (var partitionFileEntry in remainingEntries)
            {
                IFile partitionFile;
                try
                {
                    partitionFile = partitionFileSystem.LoadFile(partitionFileEntry);
                }
                catch (Exception ex)
                {
                    OnLoadingException(ex, parentItem);
                    var partitionFileEntryItem = new PartitionFileEntryItem(partitionFileEntry, parentItem);
                    var message = LocalizationManager.Instance.Current.Keys.LoadingError_FailedToOpenPartitionFile.SafeFormat(ex.Message);
                    partitionFileEntryItem.Errors.Add(Category.Loading, message);
                    _logger.LogError(ex, message);
                    continue;
                }

                var fileName = partitionFileEntry.Name;
                if (fileName.EndsWith(".ncz", StringComparison.OrdinalIgnoreCase))
                {
                    Ncz ncz;
                    try
                    {
                        ncz = new Ncz(parentItem.KeySet, partitionFile.AsStream(), NczReadMode.Fast);
                    }
                    catch (Exception ex)
                    {
                        OnLoadingException(ex, parentItem);
                        var partitionFileEntryItem = new PartitionFileEntryItem(partitionFileEntry, parentItem);
                        var message = LocalizationManager.Instance.Current.Keys.LoadingError_FailedToLoadNcaFile.SafeFormat(ex.Message);
                        partitionFileEntryItem.Errors.Add(Category.Loading, message);
                        _logger.LogError(ex, message);
                        continue;
                    }
                    var nczItem = new NczItem(ncz, partitionFileEntry, parentItem);
                    if (ncz.NczHeader.IsUsingBlockCompression || _appSettings.OpenBlocklessCompressionNCZ)
                    {
                        BuildNcaChildItems(nczItem);
                    }
                    else
                    {
                        nczItem.Errors.Add(Category.Loading, LocalizationManager.Instance.Current.Keys.LoadingError_NczBlocklessCompressionDisabled);
                    }
                }
                else if (fileName.EndsWith(".nca", StringComparison.OrdinalIgnoreCase))
                {
                    Nca nca;
                    try
                    {
                        var fileStorage = new FileStorage(partitionFile);
                        nca = new Nca(parentItem.KeySet, fileStorage);
                    }
                    catch (Exception ex)
                    {
                        OnLoadingException(ex, parentItem);
                        var partitionFileEntryItem = new PartitionFileEntryItem(partitionFileEntry, parentItem);
                        var message = LocalizationManager.Instance.Current.Keys.LoadingError_FailedToLoadNcaFile.SafeFormat(ex.Message);
                        partitionFileEntryItem.Errors.Add(Category.Loading, message);
                        _logger.LogError(ex, message);
                        continue;
                    }

                    var ncaItem = new NcaItem(nca, partitionFileEntry, parentItem);
                    BuildNcaChildItems(ncaItem);
                }
                else
                {
                    _ = new PartitionFileEntryItem(partitionFileEntry, parentItem);
                }
            }
        }
        catch (Exception ex)
        {
            OnLoadingException(ex, parentItem);

            var message = LocalizationManager.Instance.Current.Keys.LoadingError_FailedToLoadPartitionFileSystemContent.SafeFormat(ex.Message);
            parentItem.Errors.Add(Category.Loading, message);
            _logger.LogError(ex, message);
        }
    }

    private void BuildNcaChildItems(NcaItem parentItem)
    {
        try
        {
            var nca = parentItem.Nca;
            for (var sectionIndex = 0; sectionIndex < NcaItem.MaxSections; sectionIndex++)
            {
                try
                {
                    if (!nca.Header.IsSectionEnabled(sectionIndex))
                        continue;
                }
                catch (Exception ex)
                {
                    OnLoadingException(ex, parentItem);

                    var message = LocalizationManager.Instance.Current.Keys.LoadingError_FailedToCheckIfSectionCanBeOpened.SafeFormat(ex.Message);
                    parentItem.Errors.Add(Category.Loading, message);
                    _logger.LogError(ex, message);
                    continue;
                }

                NcaFsHeader ncaFsHeader;
                try
                {
                    ncaFsHeader = nca.GetFsHeader(sectionIndex);
                }
                catch (Exception ex)
                {
                    OnLoadingException(ex, parentItem);

                    var message = LocalizationManager.Instance.Current.Keys.LoadingError_FailedToGetNcaSectionFsHeader.SafeFormat(sectionIndex, ex.Message);
                    parentItem.Errors.Add(Category.Loading, message);
                    _logger.LogError(ex, message);
                    continue;
                }


                var existsSparseLayer = ncaFsHeader.ExistsSparseLayer();
                var ncaSparseInfo = existsSparseLayer ? ncaFsHeader.GetSparseInfo() : (NcaSparseInfo?)null;

                var isPatchSection = ncaFsHeader.IsPatchSection();
                var ncaFsPatchInfo = isPatchSection ? ncaFsHeader.GetPatchInfo() : (NcaFsPatchInfo?)null;

                var sectionItem = new SectionItem(sectionIndex, ncaFsHeader, parentItem, ncaFsPatchInfo, ncaSparseInfo);

                // A patch section (BKTR) only holds the data modified by an update: it can't be opened without the base NCA,
                // which LibHac needs to rebuild the complete file system. Sparse sections, on the other hand, hold all of
                // their data and can be opened on their own.
                if (!isPatchSection)
                {
                    IFileSystem? fileSystem = null;
                    try
                    {
                        try
                        {
                            fileSystem = nca.OpenFileSystem(sectionIndex, IntegrityCheckLevel.ErrorOnInvalid);
                        }
                        catch (Exception) when (existsSparseLayer)
                        {
                            // Sparse sections are sometimes flagged with invalid hashes by LibHac: retry without integrity check
                            fileSystem = nca.OpenFileSystem(sectionIndex, IntegrityCheckLevel.IgnoreOnInvalid);
                        }
                    }
                    catch (Exception ex)
                    {
                        OnLoadingException(ex, sectionItem);

                        var message = LocalizationManager.Instance.Current.Keys.LoadingError_FailedToOpenNcaSectionFileSystem.SafeFormat(sectionIndex, ex.Message);
                        sectionItem.Errors.Add(Category.Loading, message);
                        _logger.LogError(ex, message);
                    }

                    sectionItem.FileSystem = fileSystem;

                    BuildSectionChildItems(sectionItem);
                }
            }
        }
        catch (Exception ex)
        {
            OnLoadingException(ex, parentItem);

            var message = LocalizationManager.Instance.Current.Keys.LoadingError_FailedToLoadNcaContent.SafeFormat(ex.Message);
            parentItem.Errors.Add(Category.Loading, message);
            _logger.LogError(ex, message);
        }
    }

    private void BuildSectionChildItems(SectionItem parentItem)
    {
        try
        {
            const string? ROOT_PATH = "/";

            var fileSystem = parentItem.FileSystem;
            if (fileSystem == null)
                return;

            var directoryEntries = SafeGetChildDirectoryEntries(fileSystem, ROOT_PATH, parentItem).ToArray();

            foreach (var directoryEntry in directoryEntries)
            {
                var entryName = directoryEntry.Name;
                var entryPath = directoryEntry.FullPath;

                // NACP File
                if (parentItem.ParentItem.ContentType == NcaContentType.Control && string.Equals(entryName, NacpItem.NACP_FILE_NAME, StringComparison.OrdinalIgnoreCase) && directoryEntry.Type == DirectoryEntryType.File)
                {
                    IFile nacpFile;
                    try
                    {
                        using var uniqueRefFile = new UniqueRef<IFile>();
                        fileSystem.OpenFile(ref uniqueRefFile.Ref, entryPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();
                        nacpFile = uniqueRefFile.Release();
                    }
                    catch (Exception ex)
                    {
                        OnLoadingException(ex, parentItem);
                        var directoryEntryItem = new DirectoryEntryItem(parentItem, directoryEntry);
                        var message = LocalizationManager.Instance.Current.Keys.LoadingError_FailedToOpenNacpFile.SafeFormat(ex.Message);
                        directoryEntryItem.Errors.Add(Category.Loading, message);
                        _logger.LogError(ex, message);
                        continue;
                    }

                    ApplicationControlProperty nacp;
                    IReadOnlyDictionary<NacpLanguage, NacpTitleEntry> extendedTitleEntries;
                    try
                    {
                        nacpFile.GetSize(out long nacpFileSize).ThrowIfFailure();
                        byte[] rawBytes = new byte[nacpFileSize];
                        nacpFile.Read(out _, 0, rawBytes, ReadOption.None).ThrowIfFailure();

                        byte[] nacpBytes;
                        try
                        {
                            nacpBytes = NacpTitleBlockDecompressor.DecompressIfNeeded(rawBytes);
                        }
                        catch (InvalidDataException)
                        {
                            nacpBytes = rawBytes;
                        }

                        extendedTitleEntries = NacpTitleBlockDecompressor.GetExtendedTitleEntries(rawBytes);

                        var blitStruct = new BlitStruct<ApplicationControlProperty>(1);
                        int copyLen = Math.Min(nacpBytes.Length, blitStruct.ByteSpan.Length);
                        nacpBytes.AsSpan(0, copyLen).CopyTo(blitStruct.ByteSpan);
                        nacp = blitStruct.Value;
                    }
                    catch (Exception ex)
                    {
                        OnLoadingException(ex, parentItem);
                        var directoryEntryItem = new DirectoryEntryItem(parentItem, directoryEntry);
                        var message = LocalizationManager.Instance.Current.Keys.LoadingError_FailedToLoadNacpFile.SafeFormat(ex.Message);
                        directoryEntryItem.Errors.Add(Category.Loading, message);
                        _logger.LogError(ex, message);
                        continue;
                    }

                    _ = new NacpItem(nacp, parentItem, directoryEntry, extendedTitleEntries);
                }
                // CNMT File
                else if (parentItem.ParentItem.ContentType == NcaContentType.Meta && entryName.EndsWith(".cnmt", StringComparison.OrdinalIgnoreCase) && directoryEntry.Type == DirectoryEntryType.File)
                {
                    IFile cnmtFile;
                    try
                    {
                        using var uniqueRefFile = new UniqueRef<IFile>();
                        fileSystem.OpenFile(ref uniqueRefFile.Ref, entryPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();
                        cnmtFile = uniqueRefFile.Release();
                    }
                    catch (Exception ex)
                    {
                        OnLoadingException(ex, parentItem);
                        var directoryEntryItem = new DirectoryEntryItem(parentItem, directoryEntry);
                        var message = LocalizationManager.Instance.Current.Keys.LoadingError_FailedToOpenCnmtFile.SafeFormat(ex.Message);
                        directoryEntryItem.Errors.Add(Category.Loading, message);
                        _logger.LogError(ex, message);
                        continue;
                    }

                    Cnmt cnmt;
                    try
                    {
                        cnmt = new Cnmt(cnmtFile.AsStream());
                    }
                    catch (Exception ex)
                    {
                        OnLoadingException(ex, parentItem);
                        var directoryEntryItem = new DirectoryEntryItem(parentItem, directoryEntry);
                        var message = LocalizationManager.Instance.Current.Keys.LoadingError_FailedToLoadCnmtFile.SafeFormat(ex.Message);
                        directoryEntryItem.Errors.Add(Category.Loading, message);
                        _logger.LogError(ex, message);
                        continue;
                    }

                    var cnmtItem = new CnmtItem(cnmt, parentItem, directoryEntry);

                    for (var index = 0; index < cnmt.ContentEntries.Length; index++)
                    {
                        var cnmtContentEntry = cnmt.ContentEntries[index];
                        _ = new CnmtContentEntryItem(cnmtContentEntry, cnmtItem, index);
                    }
                }
                // MAIN file
                else if (parentItem.ParentItem.ContentType == NcaContentType.Program && string.Equals(entryName, MainItem.MAIN_FILE_NAME, StringComparison.OrdinalIgnoreCase) && directoryEntry.Type == DirectoryEntryType.File)
                {
                    IFile nsoFile;
                    try
                    {
                        using var uniqueRefFile = new UniqueRef<IFile>();
                        fileSystem.OpenFile(ref uniqueRefFile.Ref, entryPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();
                        nsoFile = uniqueRefFile.Release();
                    }
                    catch (Exception ex)
                    {
                        OnLoadingException(ex, parentItem);
                        var directoryEntryItem = new DirectoryEntryItem(parentItem, directoryEntry);
                        var message = LocalizationManager.Instance.Current.Keys.LoadingError_FailedToOpenMainFile.SafeFormat(ex.Message);
                        directoryEntryItem.Errors.Add(Category.Loading, message);
                        _logger.LogError(ex, message);
                        continue;
                    }

                    NsoHeader? nsoHeader;
                    try
                    {
                        var nsoReader = new NsoReader();
                        nsoReader.Initialize(nsoFile).ThrowIfFailure();
                        nsoHeader = nsoReader.Header;
                    }
                    catch (Exception ex)
                    {
                        OnLoadingException(ex, parentItem);
                        var directoryEntryItem = new DirectoryEntryItem(parentItem, directoryEntry);
                        var message = LocalizationManager.Instance.Current.Keys.LoadingError_FailedToLoadMainFile.SafeFormat(ex.Message);
                        directoryEntryItem.Errors.Add(Category.Loading, message);
                        _logger.LogError(ex, message);
                        continue;
                    }

                    _ = new MainItem(nsoHeader.Value, parentItem, directoryEntry);
                }
                else
                {
                    var directoryEntryItem = new DirectoryEntryItem(parentItem, directoryEntry);
                    BuildDirectoryEntryChildItems(directoryEntryItem);
                }
            }
        }
        catch (Exception ex)
        {
            OnLoadingException(ex, parentItem);

            var message = LocalizationManager.Instance.Current.Keys.LoadingError_FailedToLoadSectionContent.SafeFormat(ex.Message);
            parentItem.Errors.Add(Category.Loading, message);
            _logger.LogError(ex, message);
        }
    }

    /// <summary>
    /// Second loading pass: patch (BKTR) sections can't be read on their own, because they only hold the data modified
    /// by an update; the rest of the data belongs to the base NCA of the base package. This method tries to open every
    /// section which is still closed by combining it with a base NCA: the one of the same package if the package holds
    /// both the base and the update content, otherwise the one of the base package found in the same directory.
    /// When no base NCA is found, nothing happens: the sections simply stay closed, as before.
    /// </summary>
    private void ResolvePatchSections(IItem rootItem, string packageFilePath, KeySet keySet)
    {
        try
        {
            var patchSectionItems = rootItem.FindChildrenOfType<SectionItem>(includeItem: true)
                .Where(sectionItem => sectionItem.FileSystem == null && sectionItem.SectionType.HasFlag(SectionType.Patch))
                .ToList();

            if (patchSectionItems.Count == 0)
                return;

            // 1) Base NCAs of the same package: a package can hold both the base and the update content
            TryOpenPatchSections(patchSectionItems, null);

            var remainingPatchSectionItems = patchSectionItems.Where(sectionItem => sectionItem.FileSystem == null).ToList();
            if (remainingPatchSectionItems.Count == 0)
                return;

            // 2) Base packages located in the same directory as the opened package
            var baseTitleIds = GetBaseTitleIds(rootItem);

            if (baseTitleIds.Count == 0)
            {
                // The metadata is not readable (missing keys, ...): fall back on the title id found in the file name
                AddBaseTitleId(baseTitleIds, GetTitleIdFromFileName(packageFilePath));
            }

            if (baseTitleIds.Count == 0)
            {
                _logger.LogDebug("No base title id found for «{0}»: patch sections left closed.", packageFilePath);
                return;
            }

            var basePackageFilePaths = _basePackageFinder.FindBasePackageCandidates(packageFilePath, baseTitleIds);
            if (basePackageFilePaths.Count == 0)
            {
                _logger.LogDebug("No base package candidate next to «{0}»: patch sections left closed.", packageFilePath);
                return;
            }

            var usedBasePackageContents = new List<PackageContent>();

            foreach (var basePackageFilePath in basePackageFilePaths)
            {
                if (remainingPatchSectionItems.All(sectionItem => sectionItem.FileSystem != null))
                    break;

                PackageContent? basePackageContent;
                try
                {
                    basePackageContent = PackageContent.Open(basePackageFilePath, _packageTypeAnalyzer.GetType(basePackageFilePath), keySet);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, LocalizationManager.Instance.Current.Keys.BasePackage_LoadingError.SafeFormat(basePackageFilePath, ex.Message));
                    continue;
                }

                if (basePackageContent == null)
                    continue;

                if (TryOpenPatchSections(remainingPatchSectionItems, basePackageContent))
                    usedBasePackageContents.Add(basePackageContent); // Kept alive as long as the opened file
                else
                    basePackageContent.Dispose();
            }

            if (usedBasePackageContents.Count > 0 && rootItem is ItemBase rootItemBase)
            {
                foreach (var basePackageContent in usedBasePackageContents)
                    rootItemBase.AttachDisposable(basePackageContent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LocalizationManager.Instance.Current.Keys.BasePackage_LoadingError.SafeFormat(packageFilePath, ex.Message));
        }
    }

    /// <summary>
    /// Tries to open every specified patch section by combining it with a base NCA.
    /// </summary>
    /// <returns>True if at least one section could be opened.</returns>
    private bool TryOpenPatchSections(IReadOnlyCollection<SectionItem> patchSectionItems, PackageContent? basePackageContent)
    {
        var hasResolvedSection = false;

        foreach (var patchSectionItem in patchSectionItems)
        {
            if (patchSectionItem.FileSystem != null)
                continue;

            hasResolvedSection |= TryOpenPatchSectionWithBase(patchSectionItem, basePackageContent);
        }

        return hasResolvedSection;
    }

    /// <summary>
    /// Gets the title ids of the base content referenced by the metadata of the specified package tree.
    /// </summary>
    private static HashSet<ulong> GetBaseTitleIds(IItem rootItem)
    {
        var baseTitleIds = new HashSet<ulong>();

        foreach (var cnmtItem in rootItem.FindChildrenOfType<CnmtItem>(includeItem: true))
        {
            var cnmt = cnmtItem.Cnmt;

            AddBaseTitleId(baseTitleIds, cnmt.ApplicationTitleId);
            AddBaseTitleId(baseTitleIds, cnmt.PatchTitleId);
            AddBaseTitleId(baseTitleIds, cnmt.TitleId);
        }

        return baseTitleIds;
    }

    /// <summary>
    /// Extracts the title id from a package file name («Game [0100ABCD1234567800][v65536].nsp»), or returns 0.
    /// </summary>
    private static ulong GetTitleIdFromFileName(string packageFilePath)
    {
        try
        {
            var fileName = System.IO.Path.GetFileNameWithoutExtension(packageFilePath);

            foreach (Match match in Regex.Matches(fileName, @"[0-9a-fA-F]{16}"))
            {
                if (ulong.TryParse(match.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var titleId))
                    return titleId;
            }
        }
        catch (Exception)
        {
            // Not relevant: the caller simply won't get any title id
        }

        return 0;
    }

    private static void AddBaseTitleId(HashSet<ulong> baseTitleIds, ulong titleId)
    {
        if (titleId == 0)
            return;

        // An update title id has the patch bit set: the base content is referenced without it
        baseTitleIds.Add(titleId & ~PATCH_TITLE_ID_MASK);
    }

    /// <summary>
    /// Tries to open the specified patch section by combining it with a compatible base NCA.
    /// </summary>
    /// <returns>True if the section content could be read and added to the tree.</returns>
    private bool TryOpenPatchSectionWithBase(SectionItem patchSectionItem, PackageContent? basePackageContent)
    {
        var patchNcaItem = patchSectionItem.ParentItem;
        var partitionItem = patchNcaItem.ParentItem;
        var sectionIndex = patchSectionItem.SectionIndex;

        foreach (var (baseNcaName, baseNca) in EnumerateBaseNcaCandidates(partitionItem, patchNcaItem, basePackageContent))
        {
            try
            {
                if (!baseNca.Header.IsSectionEnabled(sectionIndex))
                    continue;

                var baseFsHeader = baseNca.GetFsHeader(sectionIndex);

                if (baseFsHeader.IsPatchSection())
                    continue; // The candidate is an update NCA too, it can't act as a base

                if (baseFsHeader.ExistsSparseLayer())
                    continue; // Sparse content is update content too

                var fileSystem = baseNca.OpenFileSystemWithPatch(patchNcaItem.Nca, sectionIndex, IntegrityCheckLevel.IgnoreOnInvalid);

                patchSectionItem.FileSystem = fileSystem;
                patchSectionItem.OpenedWithBaseNcaName = baseNcaName;

                BuildSectionChildItems(patchSectionItem);

                _logger.LogInformation(LocalizationManager.Instance.Current.Keys.BasePackage_SectionOpened
                    .SafeFormat(patchNcaItem.Name, sectionIndex, baseNcaName));

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "NCA «{0}» can't be used as a base for section {1} of NCA «{2}».",
                    baseNcaName, sectionIndex, patchNcaItem.Name);
            }
        }

        _logger.LogWarning(LocalizationManager.Instance.Current.Keys.BasePackage_SectionNotOpened
            .SafeFormat(patchNcaItem.Name, sectionIndex));

        return false;
    }

    /// <summary>
    /// Enumerates the NCAs which can be used as a base for the specified patch NCA: the NCAs of the same package first,
    /// then the ones of the base package.
    /// </summary>
    private static IEnumerable<(string Name, Nca Nca)> EnumerateBaseNcaCandidates(PartitionFileSystemItemBase partitionItem, NcaItem patchNcaItem, PackageContent? basePackageContent)
    {
        foreach (var ncaItem in partitionItem.NcaChildItems)
        {
            if (ReferenceEquals(ncaItem, patchNcaItem))
                continue;

            yield return (ncaItem.Name, ncaItem.Nca);
        }

        if (basePackageContent == null)
            yield break;

        foreach (var packageNca in basePackageContent.EnumerateNcas())
            yield return (packageNca.FileName, packageNca.Nca);
    }

    private void BuildDirectoryEntryChildItems(DirectoryEntryItem parentItem)
    {
        try
        {
            if (parentItem.DirectoryEntryType == DirectoryEntryType.File)
                return;

            var currentPath = parentItem.Path;
            var directoryEntries = SafeGetChildDirectoryEntries(parentItem.ContainerSectionItem.FileSystem!, currentPath, parentItem);

            foreach (var directoryEntry in directoryEntries)
            {
                var directoryEntryItem = new DirectoryEntryItem(parentItem.ContainerSectionItem, directoryEntry, parentItem);
                BuildDirectoryEntryChildItems(directoryEntryItem);
            }
        }
        catch (Exception ex)
        {
            OnLoadingException(ex, parentItem);

            var message = LocalizationManager.Instance.Current.Keys.LoadingError_FailedToLoadDirectoryContent.SafeFormat(ex.Message);
            parentItem.Errors.Add(Category.Loading, message);
            _logger.LogError(ex, message);
        }
    }

    private IEnumerable<DirectoryEntryEx> SafeGetChildDirectoryEntries(IFileSystem fileSystem, string currentPath, IItem parentItem)
    {
        try
        {
            return fileSystem.EnumerateEntries(currentPath, "*", SearchOptions.Default);
        }
        catch (Exception ex)
        {
            OnLoadingException(ex, parentItem);

            var message = LocalizationManager.Instance.Current.Keys.LoadingError_FailedToGetFileSystemDirectoryEntries.SafeFormat(ex.Message);
            parentItem.Errors.Add(Category.Loading, message);
            _logger.LogError(ex, message);
            return Array.Empty<DirectoryEntryEx>();
        }
    }

    private void OnLoadingException(Exception ex, IItem relatedItem)
    {
        if (ex is MissingKeyException missingKeyException)
            NotifyMissingKey(missingKeyException, relatedItem);
    }


    private void NotifyMissingKey(MissingKeyException ex, IItem relatedItem)
    {
        MissingKey?.Invoke(this, new MissingKeyExceptionHandlerArgs(ex, relatedItem));
    }
}
