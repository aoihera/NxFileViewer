using System;
using System.Collections.Generic;
using Emignatik.NxFileViewer.Models.TreeItems.Impl;
using Emignatik.NxFileViewer.Utils.MVVM;

namespace Emignatik.NxFileViewer.Models.TreeItems;

public abstract class ItemBase : NotifyPropertyChangedBase, IItem
{
    private readonly List<IItem> _childItems = [];
    private readonly List<ItemError> _descendantErrors = new();
    private readonly List<IDisposable> _attachedDisposables = new();

    protected ItemBase(ItemBase? parent)
    {
        ParentItem = parent;
        parent?._childItems.Add(this);
        Errors.ErrorsChanged += OnErrorsChanged;
    }

    public abstract string Name { get; }

    public abstract string DisplayName { get; }

    public IReadOnlyList<IItem> ChildItems => _childItems;

    IItem? IItem.ParentItem => ParentItem;

    public ItemBase? ParentItem { get; }

    public abstract string LibHacTypeName { get; }

    public abstract string? Format { get; }

    public IItemErrors Errors { get; } = new ItemErrors();

    public IReadOnlyList<ItemError> DescendantErrors => _descendantErrors;

    public override string ToString()
    {
        return DisplayName;
    }

    public virtual void Dispose()
    {
        DisposeAttachedDisposables();
    }

    /// <summary>
    /// Attaches a resource whose lifetime is bound to this item: it is disposed along with the item.
    /// Used to keep alive resources shared by several descendants (e.g. the base package of an update).
    /// </summary>
    internal void AttachDisposable(IDisposable disposable)
    {
        _attachedDisposables.Add(disposable ?? throw new ArgumentNullException(nameof(disposable)));
    }

    /// <summary>
    /// Disposes every resource attached with <see cref="AttachDisposable"/>. Implementations overriding
    /// <see cref="Dispose"/> should call it.
    /// </summary>
    protected void DisposeAttachedDisposables()
    {
        foreach (var attachedDisposable in _attachedDisposables)
        {
            try
            {
                attachedDisposable.Dispose();
            }
            catch (Exception)
            {
                // Attached resources are best-effort disposed
            }
        }

        _attachedDisposables.Clear();
    }

    private void OnErrorsChanged(object sender, ErrorsChangedHandlerArgs args)
    {
        var parentTemp = ParentItem;

        while (parentTemp != null)
        {
            foreach (var addedError in args.AddedErrors)
            {
                parentTemp._descendantErrors.Add(addedError);
            }

            foreach (var removedError in args.RemovedErrors)
            {
                parentTemp._descendantErrors.Remove(removedError);
            }

            parentTemp.NotifyPropertyChanged(nameof(DescendantErrors));

            parentTemp = parentTemp.ParentItem;
        }
    }

}