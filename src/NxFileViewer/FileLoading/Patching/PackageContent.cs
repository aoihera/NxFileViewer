using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emignatik.NxFileViewer.Utils.LibHacExtensions;
using LibHac.Common.Keys;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.NSZ;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using LibHac.Tools.Ncm;

namespace Emignatik.NxFileViewer.FileLoading.Patching;

/// <summary>
/// Gives a lightweight read access to the content of a package file (NSP/NSZ/XCI/XCZ), without building the whole
/// item tree: it exposes the NCAs of the package and its CNMT metadata files.
/// This is used to read the base package of an update (the one holding the data that patch/BKTR sections rely on)
/// without paying the cost of a full load.
/// </summary>
internal sealed class PackageContent : IDisposable
{
    private readonly KeySet _keySet;
    private readonly IStorage? _storage;
    private readonly IFileSystem? _partitionFileSystem;
    private readonly List<IDisposable> _ownedResources = new();
    private readonly List<PackageNca> _ncas = new();

    private bool _ncasEnumerated;

    private PackageContent(KeySet keySet, IStorage storage, IFileSystem partitionFileSystem)
    {
        _keySet = keySet ?? throw new ArgumentNullException(nameof(keySet));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _partitionFileSystem = partitionFileSystem ?? throw new ArgumentNullException(nameof(partitionFileSystem));
    }

    /// <summary>
    /// Opens the specified package file, or returns null if it doesn't hold any readable partition.
    /// </summary>
    /// <exception cref="Exception">The package can't be opened (bad file, missing keys, ...)</exception>
    public static PackageContent? Open(string filePath, PackageType packageType, KeySet keySet)
    {
        switch (packageType)
        {
            case PackageType.NSP:
                return OpenNsp(filePath, keySet);

            case PackageType.XCI:
                return OpenXci(filePath, keySet);

            default:
                return null;
        }
    }

    private static PackageContent OpenNsp(string filePath, KeySet keySet)
    {
        var storage = new LocalStorage(filePath, FileAccess.Read);
        try
        {
            var partitionFileSystem = storage.LoadPartition();
            return new PackageContent(keySet, storage, partitionFileSystem);
        }
        catch
        {
            storage.Dispose();
            throw;
        }
    }

    private static PackageContent? OpenXci(string filePath, KeySet keySet)
    {
        var storage = new LocalStorage(filePath, FileAccess.Read);
        try
        {
            var xci = new Xci(keySet, storage);

            if (!xci.HasPartition(XciPartitionType.Secure))
            {
                storage.Dispose();
                return null;
            }

            var securePartition = xci.OpenPartition(XciPartitionType.Secure);
            return new PackageContent(keySet, storage, securePartition);
        }
        catch
        {
            storage.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Enumerates the NCAs of the package. Compressed NCAs (.ncz) are exposed through the <see cref="Ncz"/> type,
    /// which derives from <see cref="Nca"/> and decompresses on demand.
    /// Unreadable entries are silently skipped.
    /// </summary>
    public IReadOnlyList<PackageNca> EnumerateNcas()
    {
        if (_ncasEnumerated)
            return _ncas;

        _ncasEnumerated = true;

        var partitionFileSystem = _partitionFileSystem!;

        foreach (var entry in partitionFileSystem.EnumerateEntries().Where(e => e.Type == DirectoryEntryType.File))
        {
            var fileName = entry.Name;

            try
            {
                if (fileName.EndsWith(".nca", StringComparison.OrdinalIgnoreCase))
                {
                    var file = partitionFileSystem.LoadFile(entry);
                    _ownedResources.Add(file);

                    _ncas.Add(new PackageNca(fileName, new Nca(_keySet, new FileStorage(file))));
                }
                else if (fileName.EndsWith(".ncz", StringComparison.OrdinalIgnoreCase))
                {
                    var stream = partitionFileSystem.LoadFile(entry).AsStream();
                    _ownedResources.Add(stream);

                    _ncas.Add(new PackageNca(fileName, new Ncz(_keySet, stream, NczReadMode.Fast)));
                }
            }
            catch (Exception)
            {
                // Not a readable NCA/NCZ: simply ignored
            }
        }

        return _ncas;
    }

    /// <summary>
    /// Reads the CNMT metadata files of the package.
    /// </summary>
    public IReadOnlyList<Cnmt> LoadCnmts()
    {
        var cnmts = new List<Cnmt>();

        foreach (var packageNca in EnumerateNcas())
        {
            if (!packageNca.FileName.Contains("cnmt", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                if (packageNca.Nca.Header.ContentType != NcaContentType.Meta)
                    continue;

                foreach (var (sectionFileSystem, cnmtEntry) in packageNca.Nca.FindEntriesAmongSections("*.cnmt"))
                {
                    using var cnmtFile = sectionFileSystem.LoadFile(cnmtEntry);
                    cnmts.Add(new Cnmt(cnmtFile.AsStream()));
                }
            }
            catch (Exception)
            {
                // Metadata not readable: ignored, the package simply won't match any title
            }
        }

        return cnmts;
    }

    public void Dispose()
    {
        foreach (var ownedResource in _ownedResources)
        {
            try
            {
                ownedResource.Dispose();
            }
            catch (Exception)
            {
                // Nothing relevant to do here
            }
        }

        _ownedResources.Clear();
        _ncas.Clear();

        _partitionFileSystem?.Dispose();
        _storage?.Dispose();
    }
}

internal sealed class PackageNca
{
    public PackageNca(string fileName, Nca nca)
    {
        FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
        Nca = nca ?? throw new ArgumentNullException(nameof(nca));
    }

    /// <summary>
    /// Name of the NCA file inside the partition (e.g. «0123456789abcdef.nca»)
    /// </summary>
    public string FileName { get; }

    public Nca Nca { get; }
}
