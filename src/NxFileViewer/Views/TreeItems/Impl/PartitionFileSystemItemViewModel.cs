using System;
using System.Collections.Generic;
using Emignatik.NxFileViewer.Commands;
using Emignatik.NxFileViewer.Localization.Keys;
using Emignatik.NxFileViewer.Models.TreeItems.Impl;
using Emignatik.NxFileViewer.Views.ObjectPropertyViewer;
using Microsoft.Extensions.DependencyInjection;

namespace Emignatik.NxFileViewer.Views.TreeItems.Impl;

public abstract class PartitionFileSystemItemViewModel : ItemViewModel
{
    private readonly PartitionFileSystemItemBase _partitionFileSystemItem;
    private readonly IMenuItemViewModel _menuItemDumpContent;

    protected PartitionFileSystemItemViewModel(PartitionFileSystemItemBase partitionFileSystemItem, IServiceProvider serviceProvider)
        : base(partitionFileSystemItem, serviceProvider)
    {
        _partitionFileSystemItem = partitionFileSystemItem;

        var dumpPackageContentCommand = serviceProvider.GetRequiredService<IDumpPackageContentCommand>();
        dumpPackageContentCommand.PartitionItem = partitionFileSystemItem;
        _menuItemDumpContent = CreateLocalizedMenuItem(nameof(ILocalizationKeys.ContextMenu_DumpPackageContent), dumpPackageContentCommand);
    }

    public override IEnumerable<IMenuItemViewModel> GetOtherContextMenuItems()
    {
        yield return _menuItemDumpContent;
    }

    [PropertyView]
    public string PartitionType => _partitionFileSystemItem.PartitionType;

    [PropertyView]
    public int NumFiles => _partitionFileSystemItem.NbEntries;
}
