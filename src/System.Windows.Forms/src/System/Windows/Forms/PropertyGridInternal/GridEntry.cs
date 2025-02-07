﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Design;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms.Design;
using System.Windows.Forms.VisualStyles;
using static Interop;

namespace System.Windows.Forms.PropertyGridInternal
{
    /// <summary>
    ///  Base Entry for properties to be displayed in properties window.
    /// </summary>
    internal abstract partial class GridEntry : GridItem, ITypeDescriptorContext
    {
        protected static Point InvalidPoint { get; } = new(int.MinValue, int.MinValue);

        private static readonly BooleanSwitch s_pbrsAssertPropsSwitch
            = new("PbrsAssertProps", "PropertyBrowser : Assert on broken properties");

        internal static AttributeTypeSorter AttributeTypeSorter { get; } = new();

        // Type flags
        internal const int FLAG_TEXT_EDITABLE = 0x0001;
        internal const int FLAG_ENUMERABLE = 0x0002;
        internal const int FLAG_CUSTOM_PAINT = 0x0004;
        internal const int FLAG_IMMEDIATELY_EDITABLE = 0x0008;
        internal const int FLAG_CUSTOM_EDITABLE = 0x0010;
        internal const int FLAG_DROPDOWN_EDITABLE = 0x0020;
        internal const int FLAG_LABEL_BOLD = 0x0040;
        internal const int FLAG_READONLY_EDITABLE = 0x0080;
        internal const int FLAG_RENDER_READONLY = 0x0100;
        internal const int FLAG_IMMUTABLE = 0x0200;
        internal const int FLAG_FORCE_READONLY = 0x0400;
        internal const int FLAG_RENDER_PASSWORD = 0x1000;

        internal const int FLAG_DISPOSED = 0x2000;

        internal const int FL_EXPAND = 0x00010000;
        internal const int FL_EXPANDABLE = 0x00020000;
        //protected const int FL_EXPANDABLE_VALID         = 0x00040000;
        internal const int FL_EXPANDABLE_FAILED = 0x00080000;
        internal const int FL_NO_CUSTOM_PAINT = 0x00100000;
        internal const int FL_CATEGORIES = 0x00200000;
        internal const int FL_CHECKED = unchecked((int)0x80000000);

        // Rest are GridEntry constants.

        protected const int NOTIFY_RESET = 1;
        protected const int NOTIFY_CAN_RESET = 2;
        protected const int NOTIFY_DBL_CLICK = 3;
        protected const int NOTIFY_SHOULD_PERSIST = 4;
        protected const int NOTIFY_RETURN = 5;

        protected const int OUTLINE_ICON_PADDING = 5;

        protected static IComparer DisplayNameComparer { get; } = new DisplayNameSortComparer();

        private static char s_passwordReplaceChar;

        // Maximum number of characters we'll show in the property grid.  Too many characters leads
        // to bad performance.
        private const int MaximumLengthOfPropertyString = 1000;

        [Flags]
        internal enum PaintValueFlags
        {
            None = 0,
            DrawSelected = 0x1,
            FetchValue = 0x2,
            CheckShouldSerialize = 0x4,
            PaintInPlace = 0x8
        }

        private CacheItems _cacheItems;

        protected TypeConverter Converter { get; set; }
        protected UITypeEditor Editor { get; set; }

        private GridEntry _parentEntry;
        private GridEntryCollection _childCollection;
        private int _propertyDepth;
        private bool _hasFocus;
        private Rectangle _outlineRect = Rectangle.Empty;

        internal int _flags;
        protected PropertySort PropertySort;

        private Point _labelTipPoint = InvalidPoint;
        private Point _valueTipPoint = InvalidPoint;

        private static readonly object s_valueClickEvent = new();
        private static readonly object s_labelClickEvent = new();
        private static readonly object s_outlineClickEvent = new();
        private static readonly object s_valueDoubleClickEvent = new();
        private static readonly object s_labelDoubleClickEvent = new();
        private static readonly object s_outlineDoubleClickEvent = new();
        private static readonly object s_recreateChildrenEvent = new();

        private GridEntryAccessibleObject _accessibleObject;

        private bool _lastPaintWithExplorerStyle;

        private static Color InvertColor(Color color)
        {
            return Color.FromArgb(color.A, (byte)~color.R, (byte)~color.G, (byte)~color.B);
        }

        protected GridEntry(PropertyGrid owner, GridEntry peParent)
        {
            _parentEntry = peParent;
            OwnerGrid = owner;

            Debug.Assert(OwnerGrid is not null, "GridEntry w/o PropertyGrid owner, text rendering will fail.");

            if (peParent is not null)
            {
                _propertyDepth = peParent.PropertyDepth + 1;
                PropertySort = peParent.PropertySort;

                if (peParent.ForceReadOnly)
                {
                    Flags |= FLAG_FORCE_READONLY;
                }
            }
            else
            {
                _propertyDepth = -1;
            }
        }

        /// <summary>
        ///  Outline Icon padding
        /// </summary>
        internal int OutlineIconPadding
        {
            get
            {
                if (DpiHelper.IsScalingRequirementMet)
                {
                    if (GridEntryHost is not null)
                    {
                        return GridEntryHost.LogicalToDeviceUnits(OUTLINE_ICON_PADDING);
                    }
                }

                return OUTLINE_ICON_PADDING;
            }
        }

        private bool colorInversionNeededInHC
        {
            get
            {
                return SystemInformation.HighContrast && !OwnerGrid._developerOverride;
            }
        }

        public AccessibleObject AccessibilityObject
        {
            get
            {
                if (_accessibleObject is null)
                {
                    _accessibleObject = GetAccessibilityObject();
                }

                return _accessibleObject;
            }
        }

        protected virtual GridEntryAccessibleObject GetAccessibilityObject()
        {
            return new GridEntryAccessibleObject(this);
        }

        /// <summary>
        ///  Specify that this grid entry should be allowed to be merged for multi-select.
        /// </summary>
        public virtual bool AllowMerge => true;

        internal virtual bool AlwaysAllowExpand => false;

        internal virtual AttributeCollection Attributes => TypeDescriptor.GetAttributes(PropertyType);

        /// <summary>
        ///  Gets the value of the background brush to use. Override this member to cause the entry to paint it's
        ///  background in a different color. The base implementation returns null.
        /// </summary>
        protected virtual Color GetBackgroundColor() => GridEntryHost.BackColor;

        protected virtual Color LabelTextColor
            => ShouldRenderReadOnly ? GridEntryHost.GrayTextColor : GridEntryHost.GetTextColor();

        /// <summary>
        ///  The set of attributes that will be used for browse filtering
        /// </summary>
        public virtual AttributeCollection BrowsableAttributes
        {
            get => _parentEntry?.BrowsableAttributes;
            set => _parentEntry.BrowsableAttributes = value;
        }

        /// <summary>
        ///  Retrieves the component that is invoking the
        ///  method on the formatter object.  This may
        ///  return null if there is no component
        ///  responsible for the call.
        /// </summary>
        public virtual IComponent Component
        {
            get
            {
                object owner = GetValueOwner();
                if (owner is IComponent)
                {
                    return (IComponent)owner;
                }

                if (_parentEntry is not null)
                {
                    return _parentEntry.Component;
                }

                return null;
            }
        }

        protected virtual IComponentChangeService ComponentChangeService
        {
            get
            {
                return _parentEntry.ComponentChangeService;
            }
        }

        /// <summary>
        ///  Retrieves the container that contains the
        ///  set of objects this formatter may work
        ///  with.  It may return null if there is no
        ///  container, or of the formatter should not
        ///  use any outside objects.
        /// </summary>
        public virtual IContainer Container
        {
            get
            {
                IComponent component = Component;
                if (component is not null)
                {
                    ISite site = component.Site;
                    if (site is not null)
                    {
                        return site.Container;
                    }
                }

                return null;
            }
        }

        protected GridEntryCollection ChildCollection
        {
            get
            {
                if (_childCollection is null)
                {
                    _childCollection = new GridEntryCollection(this, null);
                }

                return _childCollection;
            }
            set
            {
                Debug.Assert(value is null || !Disposed, "Why are we putting new children in after we are disposed?");
                if (_childCollection != value)
                {
                    if (_childCollection is not null)
                    {
                        _childCollection.Dispose();
                        _childCollection = null;
                    }

                    _childCollection = value;
                }
            }
        }

        public int ChildCount
        {
            get
            {
                if (Children is not null)
                {
                    return Children.Count;
                }

                return 0;
            }
        }

        public virtual GridEntryCollection Children
        {
            get
            {
                if (_childCollection is null && !Disposed)
                {
                    CreateChildren();
                }

                return _childCollection;
            }
        }

        public virtual PropertyTab CurrentTab
        {
            get
            {
                if (_parentEntry is not null)
                {
                    return _parentEntry.CurrentTab;
                }

                return null;
            }
            set
            {
                if (_parentEntry is not null)
                {
                    _parentEntry.CurrentTab = value;
                }
            }
        }

        /// <summary>
        ///  Returns the default child GridEntry of this item.  Usually the default property
        ///  of the target object.
        /// </summary>
        internal virtual GridEntry DefaultChild
        {
            get
            {
                return null;
            }
            set { }
        }

        internal virtual IDesignerHost DesignerHost
        {
            get
            {
                if (_parentEntry is not null)
                {
                    return _parentEntry.DesignerHost;
                }

                return null;
            }
            set
            {
                if (_parentEntry is not null)
                {
                    _parentEntry.DesignerHost = value;
                }
            }
        }

        internal bool Disposed
        {
            get
            {
                return GetFlagSet(FLAG_DISPOSED);
            }
        }

        internal virtual bool Enumerable
        {
            get
            {
                return (Flags & FLAG_ENUMERABLE) != 0;
            }
        }

        public override bool Expandable
        {
            get
            {
                bool fExpandable = GetFlagSet(FL_EXPANDABLE);

                if (fExpandable && _childCollection is not null && _childCollection.Count > 0)
                {
                    return true;
                }

                if (GetFlagSet(FL_EXPANDABLE_FAILED))
                {
                    return false;
                }

                if (fExpandable && (_cacheItems is null || _cacheItems.LastValue is null) && PropertyValue is null)
                {
                    fExpandable = false;
                }

                return fExpandable;
            }
        }

        public override bool Expanded
        {
            get
            {
                return InternalExpanded;
            }
            set
            {
                GridEntryHost.SetExpand(this, value);
            }
        }

        internal virtual bool ForceReadOnly
        {
            get
            {
                return (Flags & FLAG_FORCE_READONLY) != 0;
            }
        }

        internal virtual bool InternalExpanded
        {
            get
            {
                // short circuit if we don't have children
                if (_childCollection is null || _childCollection.Count == 0)
                {
                    return false;
                }

                return GetFlagSet(FL_EXPAND);
            }
            set
            {
                if (!Expandable || value == InternalExpanded)
                {
                    return;
                }

                if (_childCollection is not null && _childCollection.Count > 0)
                {
                    SetFlag(FL_EXPAND, value);
                }
                else
                {
                    SetFlag(FL_EXPAND, false);
                    if (value)
                    {
                        bool fMakeSure = CreateChildren();
                        SetFlag(FL_EXPAND, fMakeSure);
                    }
                }

                // Notify accessibility clients of expanded state change
                // StateChange requires NameChange, too - accessible clients won't see this, unless both events are raised

                // Root item is hidden and should not raise events
                if (GridItemType != GridItemType.Root)
                {
                    int id = ((PropertyGridView)GridEntryHost).AccessibilityGetGridEntryChildID(this);
                    if (id >= 0)
                    {
                        PropertyGridView.PropertyGridViewAccessibleObject gridAccObj =
                            (PropertyGridView.PropertyGridViewAccessibleObject)((PropertyGridView)GridEntryHost).AccessibilityObject;

                        gridAccObj.NotifyClients(AccessibleEvents.StateChange, id);
                        gridAccObj.NotifyClients(AccessibleEvents.NameChange, id);
                    }
                }
            }
        }

        internal virtual int Flags
        {
            get
            {
                if ((_flags & FL_CHECKED) != 0)
                {
                    return _flags;
                }

                _flags |= FL_CHECKED;

                TypeConverter converter = TypeConverter;
                UITypeEditor uiEditor = UITypeEditor;
                object value = Instance;
                bool forceReadOnly = ForceReadOnly;

                if (value is not null)
                {
                    forceReadOnly |= TypeDescriptor.GetAttributes(value).Contains(InheritanceAttribute.InheritedReadOnly);
                }

                if (converter.GetStandardValuesSupported(this))
                {
                    _flags |= FLAG_ENUMERABLE;
                }

                if (!forceReadOnly && converter.CanConvertFrom(this, typeof(string)) &&
                    !converter.GetStandardValuesExclusive(this))
                {
                    _flags |= FLAG_TEXT_EDITABLE;
                }

                bool isImmutableReadOnly = TypeDescriptor.GetAttributes(PropertyType)[typeof(ImmutableObjectAttribute)]
                    .Equals(ImmutableObjectAttribute.Yes);
                bool isImmutable = isImmutableReadOnly || converter.GetCreateInstanceSupported(this);

                if (isImmutable)
                {
                    _flags |= FLAG_IMMUTABLE;
                }

                if (converter.GetPropertiesSupported(this))
                {
                    _flags |= FL_EXPANDABLE;

                    // If we're expandable, but we don't support editing,
                    // make us read only editable so we don't paint grey.
                    //
                    if (!forceReadOnly && (Flags & FLAG_TEXT_EDITABLE) == 0 && !isImmutableReadOnly)
                    {
                        _flags |= FLAG_READONLY_EDITABLE;
                    }
                }

                if (Attributes.Contains(PasswordPropertyTextAttribute.Yes))
                {
                    _flags |= FLAG_RENDER_PASSWORD;
                }

                if (uiEditor is not null)
                {
                    if (uiEditor.GetPaintValueSupported(this))
                    {
                        _flags |= FLAG_CUSTOM_PAINT;
                    }

                    // We only allow drop-downs if the object is NOT being inherited
                    // I would really rather this not be here, but we have other places where
                    // we make read-only properties editable if they have drop downs.  Not
                    // sure this is the right thing...is it?

                    bool allowButtons = !forceReadOnly;

                    if (allowButtons)
                    {
                        switch (uiEditor.GetEditStyle(this))
                        {
                            case UITypeEditorEditStyle.Modal:
                                _flags |= FLAG_CUSTOM_EDITABLE;
                                if (!isImmutable && !PropertyType.IsValueType)
                                {
                                    _flags |= FLAG_READONLY_EDITABLE;
                                }

                                break;
                            case UITypeEditorEditStyle.DropDown:
                                _flags |= FLAG_DROPDOWN_EDITABLE;
                                break;
                        }
                    }
                }

                return _flags;
            }
            set
            {
                _flags = value;
            }
        }

        /// <summary>
        ///  Checks if the entry is currently expanded
        /// </summary>
        public bool HasFocus
        {
            get => _hasFocus;
            set
            {
                if (Disposed)
                {
                    return;
                }

                if (_cacheItems is not null)
                {
                    _cacheItems.LastValueString = null;
                    _cacheItems.UseValueString = false;
                    _cacheItems.UseShouldSerialize = false;
                }

                if (_hasFocus != value)
                {
                    _hasFocus = value;

                    // Notify accessibility applications that keyboard focus has changed.
                    //
                    if (value == true)
                    {
                        int id = ((PropertyGridView)GridEntryHost).AccessibilityGetGridEntryChildID(this);
                        if (id >= 0)
                        {
                            PropertyGridView.PropertyGridViewAccessibleObject gridAccObj =
                                (PropertyGridView.PropertyGridViewAccessibleObject)((PropertyGridView)GridEntryHost).AccessibilityObject;

                            gridAccObj.NotifyClients(AccessibleEvents.Focus, id);
                            gridAccObj.NotifyClients(AccessibleEvents.Selection, id);

                            AccessibilityObject.SetFocus();
                        }
                    }
                }
            }
        }

        /// <summary>
        ///  Returns the label including the object name, and properties.  For example, the value
        ///  of the Font size property on a Button called Button1 would be "Button1.Font.Size"
        /// </summary>
        public string FullLabel
        {
            get
            {
                string str = null;
                if (_parentEntry is not null)
                {
                    str = _parentEntry.FullLabel;
                }

                if (str is not null)
                {
                    str += ".";
                }
                else
                {
                    str = string.Empty;
                }

                str += PropertyLabel;

                return str;
            }
        }

        public override GridItemCollection GridItems
        {
            get
            {
                if (Disposed)
                {
                    throw new ObjectDisposedException(SR.GridItemDisposed);
                }

                if (IsExpandable && _childCollection is not null && _childCollection.Count == 0)
                {
                    CreateChildren();
                }

                return Children;
            }
        }

        internal virtual PropertyGridView GridEntryHost
        {
            get
            {        // ACCESSOR: virtual was missing from this get
                if (_parentEntry is not null)
                {
                    return _parentEntry.GridEntryHost;
                }

                return null;
            }
            set
            {
                throw new NotSupportedException();
            }
        }

        public override GridItemType GridItemType
        {
            get
            {
                return GridItemType.Property;
            }
        }

        /// <summary>
        ///  Returns true if this GridEntry has a value field in the right hand column.
        /// </summary>
        internal virtual bool HasValue
        {
            get
            {
                return true;
            }
        }

        /// <summary>
        ///  Retrieves the keyword that the VS help dynamic help window will
        ///  use when this IPE is selected.
        /// </summary>
        public virtual string HelpKeyword
        {
            get
            {
                string keyWord = null;

                if (_parentEntry is not null)
                {
                    keyWord = _parentEntry.HelpKeyword;
                }

                if (keyWord is null)
                {
                    keyWord = string.Empty;
                }

                return keyWord;
            }
        }

        internal virtual string HelpKeywordInternal
        {
            get
            {
                return HelpKeyword;
            }
        }

        public virtual bool IsCustomPaint
        {
            get
            {
                // prevent full flag population if possible.
                if ((Flags & FL_CHECKED) == 0)
                {
                    UITypeEditor typeEd = UITypeEditor;
                    if (typeEd is not null)
                    {
                        if ((Flags & FLAG_CUSTOM_PAINT) != 0 ||
                            (Flags & FL_NO_CUSTOM_PAINT) != 0)
                        {
                            return (Flags & FLAG_CUSTOM_PAINT) != 0;
                        }

                        if (typeEd.GetPaintValueSupported(this))
                        {
                            Flags |= FLAG_CUSTOM_PAINT;
                            return true;
                        }
                        else
                        {
                            Flags |= FL_NO_CUSTOM_PAINT;
                            return false;
                        }
                    }
                }

                return (Flags & FLAG_CUSTOM_PAINT) != 0;
            }
        }

        public virtual bool IsExpandable
        {
            get
            {
                return Expandable;
            }
            set
            {
                if (value != GetFlagSet(FL_EXPANDABLE))
                {
                    SetFlag(FL_EXPANDABLE_FAILED, false);
                    SetFlag(FL_EXPANDABLE, value);
                }
            }
        }

        public virtual bool IsTextEditable
        {
            get
            {
                return IsValueEditable && (Flags & FLAG_TEXT_EDITABLE) != 0;
            }
        }

        public virtual bool IsValueEditable
        {
            get
            {
                return !ForceReadOnly && 0 != (Flags & (FLAG_DROPDOWN_EDITABLE | FLAG_TEXT_EDITABLE | FLAG_CUSTOM_EDITABLE | FLAG_ENUMERABLE));
            }
        }

        /// <summary>
        ///  Retrieves the component that is invoking the
        ///  method on the formatter object.  This may
        ///  return null if there is no component
        ///  responsible for the call.
        /// </summary>
        public virtual object Instance
        {
            get
            {
                object owner = GetValueOwner();

                if (_parentEntry is not null && owner is null)
                {
                    return _parentEntry.Instance;
                }

                return owner;
            }
        }

        public override string Label
        {
            get
            {
                return PropertyLabel;
            }
        }

        /// <summary>
        ///  Retrieves the PropertyDescriptor that is surfacing the given object/
        /// </summary>
        public override PropertyDescriptor PropertyDescriptor
        {
            get
            {
                return null;
            }
        }

        /// <summary>
        ///  Returns the pixel indent of the current GridEntry's label.
        /// </summary>
        internal virtual int PropertyLabelIndent
        {
            get
            {
                int borderWidth = GridEntryHost.GetOutlineIconSize() + OutlineIconPadding;
                return ((_propertyDepth + 1) * borderWidth) + 1;
            }
        }

        internal virtual Point GetLabelToolTipLocation(int mouseX, int mouseY)
        {
            return _labelTipPoint;
        }

        internal virtual string LabelToolTipText
        {
            get
            {
                return PropertyLabel;
            }
        }

        public virtual bool NeedsDropDownButton
        {
            get
            {
                return (Flags & FLAG_DROPDOWN_EDITABLE) != 0;
            }
        }

        public virtual bool NeedsCustomEditorButton
        {
            get
            {
                return (Flags & FLAG_CUSTOM_EDITABLE) != 0 && (IsValueEditable || (Flags & FLAG_READONLY_EDITABLE) != 0);
            }
        }

        public PropertyGrid OwnerGrid { get; }

        /// <summary>
        ///  Returns rect that the outline icon (+ or - or arrow) will be drawn into, relative
        ///  to the upper left corner of the GridEntry.
        /// </summary>
        public Rectangle OutlineRect
        {
            get
            {
                if (!_outlineRect.IsEmpty)
                {
                    return _outlineRect;
                }

                PropertyGridView gridHost = GridEntryHost;
                Debug.Assert(gridHost is not null, "No propEntryHost!");
                int outlineSize = gridHost.GetOutlineIconSize();
                int borderWidth = outlineSize + OutlineIconPadding;
                int left = (_propertyDepth * borderWidth) + (OutlineIconPadding) / 2;
                int top = (gridHost.GetGridEntryHeight() - outlineSize) / 2;
                _outlineRect = new Rectangle(left, top, outlineSize, outlineSize);
                return _outlineRect;
            }
            set
            {
                // set property is required to reset cached value when dpi changed.
                if (value != _outlineRect)
                {
                    _outlineRect = value;
                }
            }
        }

        public virtual GridEntry ParentGridEntry
        {
            get
            {
                return _parentEntry;
            }
            set
            {
                Debug.Assert(value != this, "how can we be our own parent?");
                _parentEntry = value;
                if (value is not null)
                {
                    _propertyDepth = value.PropertyDepth + 1;

                    // Microsoft, why do we do this?
                    if (_childCollection is not null)
                    {
                        for (int i = 0; i < _childCollection.Count; i++)
                        {
                            _childCollection.GetEntry(i).ParentGridEntry = this;
                        }
                    }
                }
            }
        }

        public override GridItem Parent
        {
            get
            {
                if (Disposed)
                {
                    throw new ObjectDisposedException(SR.GridItemDisposed);
                }

                GridItem parent = ParentGridEntry;

                // don't allow walking all the way up to the parent.
                //
                //if (parent is IRootGridEntry) {
                //    return null;
                //}
                return parent;
            }
        }

        /// <summary>
        ///  Returns category name of the current property
        /// </summary>
        public virtual string PropertyCategory
        {
            get
            {
                return CategoryAttribute.Default.Category;
            }
        }

        /// <summary>
        ///  Returns "depth" of this property.  That is, how many parent's between
        ///  this property and the root property.  The root property has a depth of -1.
        /// </summary>
        public virtual int PropertyDepth
        {
            get
            {
                return _propertyDepth;
            }
        }

        /// <summary>
        ///  Returns the description helpstring for this GridEntry.
        /// </summary>
        public virtual string PropertyDescription
        {
            get
            {
                return null;
            }
        }

        /// <summary>
        ///  Returns the label of this property.  Usually
        ///  this is the property name.
        /// </summary>
        public virtual string PropertyLabel
        {
            get
            {
                return null;
            }
        }

        /// <summary>
        ///  Returns non-localized name of this property.
        /// </summary>
        public virtual string PropertyName
        {
            get
            {
                return PropertyLabel;
            }
        }

        /// <summary>
        ///  Returns the Type of the value of this GridEntry, or null if the value is null.
        /// </summary>
        public virtual Type PropertyType
        {
            get
            {
                object obj = PropertyValue;
                if (obj is not null)
                {
                    return obj.GetType();
                }
                else
                {
                    return null;
                }
            }
        }

        /// <summary>
        ///  Gets or sets the value for the property that is represented
        ///  by this GridEntry.
        /// </summary>
        public virtual object PropertyValue
        {
            get
            {
                if (_cacheItems is not null)
                {
                    return _cacheItems.LastValue;
                }

                return null;
            }
            set
            {
            }
        }

        public virtual bool ShouldRenderPassword
        {
            get
            {
                return (Flags & FLAG_RENDER_PASSWORD) != 0;
            }
        }

        public virtual bool ShouldRenderReadOnly
        {
            get
            {
                return ForceReadOnly || (0 != (Flags & FLAG_RENDER_READONLY) || (!IsValueEditable && (0 == (Flags & FLAG_READONLY_EDITABLE))));
            }
        }

        /// <summary>
        ///  Returns the type converter for this entry.
        /// </summary>
        internal virtual TypeConverter TypeConverter
        {
            get
            {
                if (Converter is null)
                {
                    object value = PropertyValue;
                    if (value is null)
                    {
                        Converter = TypeDescriptor.GetConverter(PropertyType);
                    }
                    else
                    {
                        Converter = TypeDescriptor.GetConverter(value);
                    }
                }

                return Converter;
            }
        }

        /// <summary>
        ///  Returns the type editor for this entry.  This may return null if there
        ///  is no type editor.
        /// </summary>
        internal virtual UITypeEditor UITypeEditor
        {
            get
            {
                if (Editor is null && PropertyType is not null)
                {
                    Editor = (UITypeEditor)TypeDescriptor.GetEditor(PropertyType, typeof(UITypeEditor));
                }

                return Editor;
            }
        }

        public override object Value
        {
            get
            {
                return PropertyValue;
            }

            // note: we don't do set because of the value class semantics, etc.
        }

        internal Point ValueToolTipLocation
        {
            get
            {
                return ShouldRenderPassword ? InvalidPoint : _valueTipPoint;
            }
            set
            {
                _valueTipPoint = value;
            }
        }

        internal int VisibleChildCount
        {
            get
            {
                if (!Expanded)
                {
                    return 0;
                }

                int count = ChildCount;
                int totalCount = count;
                for (int i = 0; i < count; i++)
                {
                    totalCount += ChildCollection.GetEntry(i).VisibleChildCount;
                }

                return totalCount;
            }
        }

        /// <summary>
        ///  Add an event handler to be invoked when the label portion of
        ///  the prop entry is clicked
        /// </summary>
        public virtual void AddOnLabelClick(EventHandler h)
        {
            AddEventHandler(s_labelClickEvent, h);
        }

        /// <summary>
        ///  Add an event handler to be invoked when the label portion of
        ///  the prop entry is double
        /// </summary>
        public virtual void AddOnLabelDoubleClick(EventHandler h)
        {
            AddEventHandler(s_labelDoubleClickEvent, h);
        }

        /// <summary>
        ///  Add an event handler to be invoked when the value portion of
        ///  the prop entry is clicked
        /// </summary>
        public virtual void AddOnValueClick(EventHandler h)
        {
            AddEventHandler(s_valueClickEvent, h);
        }

        /// <summary>
        ///  Add an event handler to be invoked when the value portion of
        ///  the prop entry is double-clicked
        /// </summary>
        public virtual void AddOnValueDoubleClick(EventHandler h)
        {
            AddEventHandler(s_valueDoubleClickEvent, h);
        }

        /// <summary>
        ///  Add an event handler to be invoked when the outline icon portion of
        ///  the prop entry is clicked
        /// </summary>
        public virtual void AddOnOutlineClick(EventHandler h)
        {
            AddEventHandler(s_outlineClickEvent, h);
        }

        /// <summary>
        ///  Add an event handler to be invoked when the outline icon portion of
        ///  the prop entry is double clicked
        /// </summary>
        public virtual void AddOnOutlineDoubleClick(EventHandler h)
        {
            AddEventHandler(s_outlineDoubleClickEvent, h);
        }

        /// <summary>
        ///  Add an event handler to be invoked when the children grid entries are re-created.
        /// </summary>
        public virtual void AddOnRecreateChildren(GridEntryRecreateChildrenEventHandler h)
        {
            AddEventHandler(s_recreateChildrenEvent, h);
        }

        internal void ClearCachedValues()
        {
            ClearCachedValues(true);
        }

        internal void ClearCachedValues(bool clearChildren)
        {
            if (_cacheItems is not null)
            {
                _cacheItems.UseValueString = false;
                _cacheItems.LastValue = null;
                _cacheItems.UseShouldSerialize = false;
            }

            if (clearChildren)
            {
                for (int i = 0; i < ChildCollection.Count; i++)
                {
                    ChildCollection.GetEntry(i).ClearCachedValues();
                }
            }
        }

        /// <summary>
        ///  Converts the given string of text to a value.
        /// </summary>
        public object ConvertTextToValue(string text)
        {
            if (TypeConverter.CanConvertFrom(this, typeof(string)))
            {
                return TypeConverter.ConvertFromString(this, text);
            }

            return text;
        }

        /// <summary>
        ///  Create the base prop entries given an object or set of objects
        /// </summary>
        internal static IRootGridEntry Create(PropertyGridView view, object[] rgobjs, IServiceProvider baseProvider, IDesignerHost currentHost, PropertyTab tab, PropertySort initialSortType)
        {
            IRootGridEntry pe = null;

            if (rgobjs is null || rgobjs.Length == 0)
            {
                return null;
            }

            try
            {
                if (rgobjs.Length == 1)
                {
                    pe = new SingleSelectRootGridEntry(view, rgobjs[0], baseProvider, currentHost, tab, initialSortType);
                }
                else
                {
                    pe = new MultiSelectRootGridEntry(view, rgobjs, baseProvider, currentHost, tab, initialSortType);
                }
            }
            catch (Exception e)
            {
                Debug.Fail(e.ToString());
                throw;
            }

            return pe;
        }

        /// <summary>
        ///  Populates the children of this grid entry
        /// </summary>
        protected virtual bool CreateChildren()
        {
            return CreateChildren(false);
        }

        /// <summary>
        ///  Populates the children of this grid entry
        /// </summary>
        protected virtual bool CreateChildren(bool diffOldChildren)
        {
            Debug.Assert(!Disposed, "Why are we creating children after we are disposed?");

            if (!GetFlagSet(FL_EXPANDABLE))
            {
                if (_childCollection is not null)
                {
                    _childCollection.Clear();
                }
                else
                {
                    _childCollection = new GridEntryCollection(this, Array.Empty<GridEntry>());
                }

                return false;
            }

            if (!diffOldChildren && _childCollection is not null && _childCollection.Count > 0)
            {
                return true;
            }

            GridEntry[] childProps = GetPropEntries(this,
                                                        PropertyValue,
                                                        PropertyType);

            bool fExpandable = (childProps is not null && childProps.Length > 0);

            if (diffOldChildren && _childCollection is not null && _childCollection.Count > 0)
            {
                bool same = true;
                if (childProps.Length == _childCollection.Count)
                {
                    for (int i = 0; i < childProps.Length; i++)
                    {
                        if (!childProps[i].NonParentEquals(_childCollection[i]))
                        {
                            same = false;
                            break;
                        }
                    }
                }
                else
                {
                    same = false;
                }

                if (same)
                {
                    return true;
                }
            }

            if (!fExpandable)
            {
                SetFlag(FL_EXPANDABLE_FAILED, true);
                if (_childCollection is not null)
                {
                    _childCollection.Clear();
                }
                else
                {
                    _childCollection = new GridEntryCollection(this, Array.Empty<GridEntry>());
                }

                if (InternalExpanded)
                {
                    InternalExpanded = false;
                }
            }
            else
            {
                if (_childCollection is not null)
                {
                    _childCollection.Clear();
                    _childCollection.AddRange(childProps);
                }
                else
                {
                    _childCollection = new GridEntryCollection(this, childProps);
                }
            }

            return fExpandable;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            // Make sure we don't accidentally check flags while disposing.
            _flags |= FL_CHECKED;

            SetFlag(FLAG_DISPOSED, true);

            _cacheItems = null;
            Converter = null;
            Editor = null;
            _accessibleObject = null;

            if (disposing)
            {
                DisposeChildren();
            }
        }

        /// <summary>
        ///  Disposes the array of children
        /// </summary>
        public virtual void DisposeChildren()
        {
            if (_childCollection is not null)
            {
                _childCollection.Dispose();
                _childCollection = null;
            }
        }

        ~GridEntry()
        {
            Dispose(false);
        }

        /// <summary>
        ///  Invokes the type editor for editing this item.
        /// </summary>
        internal virtual void EditPropertyValue(PropertyGridView iva)
        {
            if (UITypeEditor is not null)
            {
                try
                {
                    // Since edit value can push a modal loop
                    // there is a chance that this gridentry will be zombied before
                    // it returns.  Make sure we're not disposed.
                    //
                    object originalValue = PropertyValue;
                    object value = UITypeEditor.EditValue(this, (IServiceProvider)(ITypeDescriptorContext)this, originalValue);

                    if (Disposed)
                    {
                        return;
                    }

                    // Push the new value back into the property
                    if (value != originalValue && IsValueEditable)
                    {
                        iva.CommitValue(this, value);
                    }

                    if (InternalExpanded)
                    {
                        // QFE#3299: If edited property is expanded to show sub-properties, then we want to
                        // preserve the expanded states of it and all of its descendants. RecreateChildren()
                        // has logic that is supposed to do this, but which is fundamentally flawed.
                        PropertyGridView.GridPositionData positionData = GridEntryHost.CaptureGridPositionData();
                        InternalExpanded = false;
                        RecreateChildren();
                        positionData.Restore(GridEntryHost);
                    }
                    else
                    {
                        // If edited property has no children or is collapsed, don't need to preserve expanded states.
                        // This limits the scope of the above QFE fix to just those cases where it is actually required.
                        RecreateChildren();
                    }
                }
                catch (Exception e)
                {
                    IUIService uiSvc = (IUIService)GetService(typeof(IUIService));
                    if (uiSvc is not null)
                    {
                        uiSvc.ShowError(e);
                    }
                    else
                    {
                        RTLAwareMessageBox.Show(GridEntryHost, e.Message, SR.PBRSErrorTitle, MessageBoxButtons.OK,
                                MessageBoxIcon.Error, MessageBoxDefaultButton.Button1, 0);
                    }
                }
            }
        }

        /// <summary>
        ///  Tests two GridEntries for equality
        /// </summary>
        public override bool Equals(object obj)
        {
            if (NonParentEquals(obj))
            {
                return ((GridEntry)obj).ParentGridEntry == ParentGridEntry;
            }

            return false;
        }

        /// <summary>
        ///  Searches for a value of a given property for a value editor user
        /// </summary>
        public virtual object FindPropertyValue(string propertyName, Type propertyType)
        {
            object owner = GetValueOwner();
            PropertyDescriptor property = TypeDescriptor.GetProperties(owner)[propertyName];
            if (property is not null && property.PropertyType == propertyType)
            {
                return property.GetValue(owner);
            }

            if (_parentEntry is not null)
            {
                return _parentEntry.FindPropertyValue(propertyName, propertyType);
            }

            return null;
        }

        /// <summary>
        ///  Returns the index of a child GridEntry
        /// </summary>
        internal virtual int GetChildIndex(GridEntry pe)
        {
            return Children.GetEntry(pe);
        }

        /// <summary>
        ///  Gets the components that own the current value.  This is usually the value of the
        ///  root entry, which is the object being browsed.  Walks up the GridEntry tree
        ///  looking for an owner that is an IComponent
        /// </summary>
        public virtual IComponent[] GetComponents()
        {
            IComponent component = Component;
            if (component is not null)
            {
                return new IComponent[] { component };
            }

            return null;
        }

        protected int GetLabelTextWidth(string labelText, Graphics g, Font f)
        {
            if (_cacheItems is null)
            {
                _cacheItems = new CacheItems();
            }
            else if (_cacheItems.UseCompatTextRendering == OwnerGrid.UseCompatibleTextRendering && _cacheItems.LastLabel == labelText && f.Equals(_cacheItems.LastLabelFont))
            {
                return _cacheItems.LastLabelWidth;
            }

            SizeF textSize = PropertyGrid.MeasureTextHelper.MeasureText(OwnerGrid, g, labelText, f);

            _cacheItems.LastLabelWidth = (int)textSize.Width;
            _cacheItems.LastLabel = labelText;
            _cacheItems.LastLabelFont = f;
            _cacheItems.UseCompatTextRendering = OwnerGrid.UseCompatibleTextRendering;

            return _cacheItems.LastLabelWidth;
        }

        internal int GetValueTextWidth(string valueString, Graphics g, Font f)
        {
            if (_cacheItems is null)
            {
                _cacheItems = new CacheItems();
            }
            else if (_cacheItems.LastValueTextWidth != -1 && _cacheItems.LastValueString == valueString && f.Equals(_cacheItems.LastValueFont))
            {
                return _cacheItems.LastValueTextWidth;
            }

            // Value text is rendered using GDI directly (No TextRenderer) but measured/adjusted using GDI+ (since previous releases), so don't use MeasureTextHelper.
            _cacheItems.LastValueTextWidth = (int)g.MeasureString(valueString, f).Width;
            _cacheItems.LastValueString = valueString;
            _cacheItems.LastValueFont = f;
            return _cacheItems.LastValueTextWidth;
        }

        // To check if text contains multiple lines
        //
        internal bool GetMultipleLines(string valueString)
        {
            if (valueString.IndexOf('\n') > 0 || valueString.IndexOf('\r') > 0)
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        ///  Gets the owner of the current value.  This is usually the value of the
        ///  root entry, which is the object being browsed
        /// </summary>
        public virtual object GetValueOwner()
        {
            if (_parentEntry is null)
            {
                return PropertyValue;
            }

            return _parentEntry.GetChildValueOwner(this);
        }

        /// <summary>
        ///  Gets the owners of the current value.  This is usually the value of the
        ///  root entry, which is the objects being browsed for a multiselect item
        /// </summary>
        public virtual object[] GetValueOwners()
        {
            object owner = GetValueOwner();
            if (owner is not null)
            {
                return new object[] { owner };
            }

            return null;
        }

        /// <summary>
        ///  Gets the owner of the current value.  This is usually the value of the
        ///  root entry, which is the object being browsed
        /// </summary>
        public virtual object GetChildValueOwner(GridEntry childEntry)
        {
            /*// make sure this is one of our children
            int index = GetChildIndex(childEntry);

            if (index != -1){
               return this.PropertyValue;
            }

            Debug.Fail(childEntry.PropertyLabel + " is not a child of " + this.PropertyLabel);
            return null;*/
            return PropertyValue;
        }

        /// <summary>
        ///  Returns a string with info about the currently selected GridEntry
        /// </summary>
        public virtual string GetTestingInfo()
        {
            string str = "object = (";
            string textVal = GetPropertyTextValue();
            if (textVal is null)
            {
                textVal = "(null)";
            }
            else
            {
                // make sure we clear any embedded nulls
                textVal = textVal.Replace((char)0, ' ');
            }

            Type type = PropertyType;
            if (type is null)
            {
                type = typeof(object);
            }

            str += FullLabel;
            str += "), property = (" + PropertyLabel + "," + type.AssemblyQualifiedName + "), value = " + "[" + textVal + "], expandable = " + Expandable.ToString() + ", readOnly = " + ShouldRenderReadOnly;
            ;
            return str;
        }

        /// <summary>
        ///  Retrieves the type of the value for this GridEntry
        /// </summary>
        public virtual Type GetValueType()
        {
            return PropertyType;
        }

        /// <summary>
        ///  Returns the child GridEntries for this item.
        /// </summary>
        protected virtual GridEntry[] GetPropEntries(GridEntry peParent, object obj, Type objType)
        {
            // we don't want to create subprops for null objects.
            if (obj is null)
            {
                return null;
            }

            GridEntry[] entries = null;

            Attribute[] attributes = new Attribute[BrowsableAttributes.Count];
            BrowsableAttributes.CopyTo(attributes, 0);

            PropertyTab tab = CurrentTab;
            Debug.Assert(tab is not null, "No current tab!");

            try
            {
                bool forceReadOnly = ForceReadOnly;

                if (!forceReadOnly)
                {
                    ReadOnlyAttribute readOnlyAttr = (ReadOnlyAttribute)TypeDescriptor.GetAttributes(obj)[typeof(ReadOnlyAttribute)];
                    forceReadOnly = (readOnlyAttr is not null && !readOnlyAttr.IsDefaultAttribute());
                }

                // do we want to expose sub properties?
                //
                if (TypeConverter.GetPropertiesSupported(this) || AlwaysAllowExpand)
                {
                    // ask the tab if we have one.
                    //
                    PropertyDescriptorCollection props = null;
                    PropertyDescriptor defProp = null;
                    if (tab is not null)
                    {
                        props = tab.GetProperties(this, obj, attributes);
                        defProp = tab.GetDefaultProperty(obj);
                    }
                    else
                    {
                        props = TypeConverter.GetProperties(this, obj, attributes);
                        defProp = TypeDescriptor.GetDefaultProperty(obj);
                    }

                    if (props is null)
                    {
                        return null;
                    }

                    if ((PropertySort & PropertySort.Alphabetical) != 0)
                    {
                        if (objType is null || !objType.IsArray)
                        {
                            props = props.Sort(DisplayNameComparer);
                        }

                        PropertyDescriptor[] propertyDescriptors = new PropertyDescriptor[props.Count];
                        props.CopyTo(propertyDescriptors, 0);

                        props = new PropertyDescriptorCollection(SortParenProperties(propertyDescriptors));
                    }

                    if (defProp is null && props.Count > 0)
                    {
                        defProp = props[0];
                    }

                    // if the target object is an array and nothing else has provided a set of
                    // properties to use, then expand the array.
                    //
                    if ((props is null || props.Count == 0) && objType is not null && objType.IsArray && obj is not null)
                    {
                        Array objArray = (Array)obj;

                        entries = new GridEntry[objArray.Length];

                        for (int i = 0; i < entries.Length; i++)
                        {
                            entries[i] = new ArrayElementGridEntry(OwnerGrid, peParent, i);
                        }
                    }
                    else
                    {
                        // otherwise, create the proper GridEntries.
                        //
                        bool createInstanceSupported = TypeConverter.GetCreateInstanceSupported(this);
                        entries = new GridEntry[props.Count];
                        int index = 0;

                        // loop through all the props we got and create property descriptors.
                        //
                        foreach (PropertyDescriptor pd in props)
                        {
                            GridEntry newEntry;

                            // make sure we've got a valid property, otherwise hide it
                            //
                            bool hide = false;
                            try
                            {
                                object owner = obj;
                                if (obj is ICustomTypeDescriptor)
                                {
                                    owner = ((ICustomTypeDescriptor)obj).GetPropertyOwner(pd);
                                }

                                pd.GetValue(owner);
                            }
                            catch (Exception w)
                            {
                                if (s_pbrsAssertPropsSwitch.Enabled)
                                {
                                    Debug.Fail("Bad property '" + peParent.PropertyLabel + "." + pd.Name + "': " + w.ToString());
                                }

                                hide = true;
                            }

                            if (createInstanceSupported)
                            {
                                newEntry = new ImmutablePropertyDescriptorGridEntry(OwnerGrid, peParent, pd, hide);
                            }
                            else
                            {
                                newEntry = new PropertyDescriptorGridEntry(OwnerGrid, peParent, pd, hide);
                            }

                            if (forceReadOnly)
                            {
                                newEntry.Flags |= FLAG_FORCE_READONLY;
                            }

                            // check to see if we've come across the default item.
                            //
                            if (pd.Equals(defProp))
                            {
                                DefaultChild = newEntry;
                            }

                            // add it to the array.
                            //
                            entries[index++] = newEntry;
                        }
                    }
                }
            }
            catch (Exception e)
            {
#if DEBUG
                if (s_pbrsAssertPropsSwitch.Enabled)
                {
                    // Checked builds are not giving us enough information here.  So, output as much stuff as
                    // we can.
                    Text.StringBuilder b = new Text.StringBuilder();
                    b.Append(string.Format(CultureInfo.CurrentCulture, "********* Debug log written on {0} ************\r\n", DateTime.Now));
                    b.Append(string.Format(CultureInfo.CurrentCulture, "Exception '{0}' reading properties for object {1}.\r\n", e.GetType().Name, obj));
                    b.Append(string.Format(CultureInfo.CurrentCulture, "Exception Text: \r\n{0}", e.ToString()));
                    b.Append(string.Format(CultureInfo.CurrentCulture, "Exception stack: \r\n{0}", e.StackTrace));
                    string path = string.Format(CultureInfo.CurrentCulture, "{0}\\PropertyGrid.log", Environment.GetEnvironmentVariable("SYSTEMDRIVE"));
                    IO.FileStream s = new IO.FileStream(path, IO.FileMode.Append, IO.FileAccess.Write, IO.FileShare.None);
                    IO.StreamWriter w = new IO.StreamWriter(s);
                    w.Write(b.ToString());
                    w.Close();
                    s.Close();
                    RTLAwareMessageBox.Show(null, b.ToString(), string.Format(SR.PropertyGridInternalNoProp, path),
                        MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1, 0);
                }
#endif
                Debug.Fail("Failed to get properties: " + e.GetType().Name + "," + e.Message + "\n" + e.StackTrace);
            }

            return entries;
        }

        /// <summary>
        ///  Resets the current item
        /// </summary>
        public virtual void ResetPropertyValue()
        {
            NotifyValue(NOTIFY_RESET);
            Refresh();
        }

        /// <summary>
        ///  Returns if the property can be reset
        /// </summary>
        public virtual bool CanResetPropertyValue()
        {
            return NotifyValue(NOTIFY_CAN_RESET);
        }

        /// <summary>
        ///  Called when the item is double clicked.
        /// </summary>
        public virtual bool DoubleClickPropertyValue()
        {
            return NotifyValue(NOTIFY_DBL_CLICK);
        }

        /// <summary>
        ///  Returns the text value of this property.
        /// </summary>
        public virtual string GetPropertyTextValue()
        {
            return GetPropertyTextValue(PropertyValue);
        }

        /// <summary>
        ///  Returns the text value of this property.
        /// </summary>
        public virtual string GetPropertyTextValue(object value)
        {
            string str = null;

            TypeConverter converter = TypeConverter;
            try
            {
                str = converter.ConvertToString(this, value);
            }
            catch (Exception t)
            {
                Debug.Fail("Bad Type Converter! " + t.GetType().Name + ", " + t.Message + "," + converter.ToString(), t.ToString());
            }

            if (str is null)
            {
                str = string.Empty;
            }

            return str;
        }

        /// <summary>
        ///  Returns the text values of this property.
        /// </summary>
        public virtual object[] GetPropertyValueList()
        {
            ICollection values = TypeConverter.GetStandardValues(this);
            if (values is not null)
            {
                object[] valueArray = new object[values.Count];
                values.CopyTo(valueArray, 0);
                return valueArray;
            }

            return Array.Empty<object>();
        }

        public override int GetHashCode() => HashCode.Combine(PropertyLabel, PropertyType, GetType());

        /// <summary>
        ///  Checks if a given flag is set
        /// </summary>
        protected virtual bool GetFlagSet(int flag)
        {
            return ((flag & Flags) != 0);
        }

        protected Font GetFont(bool boldFont)
        {
            if (boldFont)
            {
                return GridEntryHost.GetBoldFont();
            }
            else
            {
                return GridEntryHost.GetBaseFont();
            }
        }

        /// <summary>
        ///  Retrieves the requested service.  This may
        ///  return null if the requested service is not
        ///  available.
        /// </summary>
        public virtual object GetService(Type serviceType)
        {
            if (serviceType == typeof(GridItem))
            {
                return (GridItem)this;
            }

            if (_parentEntry is not null)
            {
                return _parentEntry.GetService(serviceType);
            }

            return null;
        }

        internal virtual bool NonParentEquals(object obj)
        {
            if (obj == this)
            {
                return true;
            }

            if (obj is null)
            {
                return false;
            }

            if (!(obj is GridEntry))
            {
                return false;
            }

            GridEntry pe = (GridEntry)obj;

            return pe.PropertyLabel.Equals(PropertyLabel) &&
                   pe.PropertyType.Equals(PropertyType) && pe.PropertyDepth == PropertyDepth;
        }

        /// <summary>
        ///  Paints the label portion of this GridEntry into the given Graphics object. This is called by the GridEntry
        ///  host (the PropertyGridView) when this GridEntry is to be painted.
        /// </summary>
        public virtual void PaintLabel(Graphics g, Rectangle rect, Rectangle clipRect, bool selected, bool paintFullLabel)
        {
            PropertyGridView gridHost = GridEntryHost;
            Debug.Assert(gridHost is not null, "No propEntryHost");
            string strLabel = PropertyLabel;

            int borderWidth = gridHost.GetOutlineIconSize() + OutlineIconPadding;

            // fill the background if necessary
            Color backColor = selected ? gridHost.GetSelectedItemWithFocusBackColor() : GetBackgroundColor();

            // if we don't have focus, paint with the line color
            if (selected && !_hasFocus)
            {
                backColor = gridHost.GetLineColor();
            }

            bool fBold = ((Flags & FLAG_LABEL_BOLD) != 0);
            Font font = GetFont(fBold);

            int labelWidth = GetLabelTextWidth(strLabel, g, font);

            int neededWidth = paintFullLabel ? labelWidth : 0;
            int stringX = rect.X + PropertyLabelIndent;

            using var backBrush = backColor.GetCachedSolidBrushScope();
            if (paintFullLabel && (neededWidth >= (rect.Width - (stringX + 2))))
            {
                // GDIPLUS_SPACE = extra needed to ensure text draws completely and isn't clipped.
                int totalWidth = stringX + neededWidth + PropertyGridView.GdiPlusSpace;

                // blank out the space we're going to use
                g.FillRectangle(backBrush, borderWidth - 1, rect.Y, totalWidth - borderWidth + 3, rect.Height);

                // draw an end line
                using var linePen = gridHost.GetLineColor().GetCachedPenScope();
                g.DrawLine(linePen, totalWidth, rect.Y, totalWidth, rect.Height);

                // set the new width that we can draw into
                rect.Width = totalWidth - rect.X;
            }
            else
            {
                // Normal case -- no pseudo-tooltip for the label
                g.FillRectangle(backBrush, rect.X, rect.Y, rect.Width, rect.Height);
            }

            // Draw the border stripe on the left
            using var stripeBrush = gridHost.GetLineColor().GetCachedSolidBrushScope();
            g.FillRectangle(stripeBrush, rect.X, rect.Y, borderWidth, rect.Height);

            if (selected && _hasFocus)
            {
                using var focusBrush = gridHost.GetSelectedItemWithFocusBackColor().GetCachedSolidBrushScope();
                g.FillRectangle(
                    focusBrush,
                    stringX, rect.Y, rect.Width - stringX - 1, rect.Height);
            }

            int maxSpace = Math.Min(rect.Width - stringX - 1, labelWidth + PropertyGridView.GdiPlusSpace);
            Rectangle textRect = new Rectangle(stringX, rect.Y + 1, maxSpace, rect.Height - 1);

            if (!Rectangle.Intersect(textRect, clipRect).IsEmpty)
            {
                Region oldClip = g.Clip;
                g.SetClip(textRect);

                // We need to Invert color only if in Highcontrast mode, targeting 4.7.1 and above, Gridcategory and
                // not a developer override. This is required to achieve required contrast ratio.
                var shouldInvertForHC = colorInversionNeededInHC && (fBold || (selected && !_hasFocus));

                // Do actual drawing
                // A brush is needed if using GDI+ only (UseCompatibleTextRendering); if using GDI, only the color is needed.
                Color textColor = selected && _hasFocus
                    ? gridHost.GetSelectedItemWithFocusForeColor()
                    : shouldInvertForHC
                        ? InvertColor(OwnerGrid.LineColor)
                        : g.FindNearestColor(LabelTextColor);

                if (OwnerGrid.UseCompatibleTextRendering)
                {
                    using var textBrush = textColor.GetCachedSolidBrushScope();
                    StringFormat stringFormat = new StringFormat(StringFormatFlags.NoWrap)
                    {
                        Trimming = StringTrimming.None
                    };
                    g.DrawString(strLabel, font, textBrush, textRect, stringFormat);
                }
                else
                {
                    TextRenderer.DrawText(g, strLabel, font, textRect, textColor, PropertyGrid.MeasureTextHelper.GetTextRendererFlags());
                }

                g.SetClip(oldClip, CombineMode.Replace);
                oldClip.Dispose();   // clip is actually copied out.

                if (maxSpace <= labelWidth)
                {
                    _labelTipPoint = new Point(stringX + 2, rect.Y + 1);
                }
                else
                {
                    _labelTipPoint = InvalidPoint;
                }
            }

            rect.Y -= 1;
            rect.Height += 2;

            PaintOutline(g, rect);
        }

        /// <summary>
        ///  Paints the outline portion of this GridEntry into the given Graphics object.  This
        ///  is called by the GridEntry host (the PropertyGridView) when this GridEntry is
        ///  to be painted.
        /// </summary>
        public virtual void PaintOutline(Graphics g, Rectangle r)
        {
            // draw tree-view glyphs as triangles on Vista and Windows afterword
            // when Visual style is enabled
            if (GridEntryHost.IsExplorerTreeSupported)
            {
                // size of Explorer Tree style glyph (triangle) is different from +/- glyph,
                // so when we change the visual style (such as changing Windows theme),
                // we need to recalculate outlineRect
                if (!_lastPaintWithExplorerStyle)
                {
                    _outlineRect = Rectangle.Empty;
                    _lastPaintWithExplorerStyle = true;
                }

                PaintOutlineWithExplorerTreeStyle(g, r, (GridEntryHost is not null) ? GridEntryHost.HandleInternal : IntPtr.Zero);
            }

            // draw tree-view glyphs as +/-
            else
            {
                // size of Explorer Tree style glyph (triangle) is different from +/- glyph,
                // so when we change the visual style (such as changing Windows theme),
                // we need to recalculate outlineRect
                if (_lastPaintWithExplorerStyle)
                {
                    _outlineRect = Rectangle.Empty;
                    _lastPaintWithExplorerStyle = false;
                }

                PaintOutlineWithClassicStyle(g, r);
            }
        }

        private void PaintOutlineWithExplorerTreeStyle(Graphics g, Rectangle r, IntPtr handle)
        {
            if (Expandable)
            {
                bool fExpanded = InternalExpanded;
                Rectangle outline = OutlineRect;

                // make sure we're in our bounds
                outline = Rectangle.Intersect(r, outline);
                if (outline.IsEmpty)
                {
                    return;
                }

                VisualStyleElement element = fExpanded
                    ? VisualStyleElement.ExplorerTreeView.Glyph.Opened
                    : VisualStyleElement.ExplorerTreeView.Glyph.Closed;

                // Invert color if it is not overriden by developer.
                if (colorInversionNeededInHC)
                {
                    Color textColor = InvertColor(OwnerGrid.LineColor);
                    if (g is not null)
                    {
                        using var brush = textColor.GetCachedSolidBrushScope();
                        g.FillRectangle(brush, outline);
                    }
                }

                VisualStyleRenderer explorerTreeRenderer = new VisualStyleRenderer(element);

                using var hdc = new DeviceContextHdcScope(g);
                explorerTreeRenderer.DrawBackground(hdc, outline, handle);
            }
        }

        private void PaintOutlineWithClassicStyle(Graphics g, Rectangle r)
        {
            // Draw outline box.
            if (Expandable)
            {
                bool fExpanded = InternalExpanded;
                Rectangle outline = OutlineRect;

                // make sure we're in our bounds
                outline = Rectangle.Intersect(r, outline);
                if (outline.IsEmpty)
                {
                    return;
                }

                // Draw border area box
                Color penColor = GridEntryHost.GetTextColor();

                // inverting text color to back ground to get required contrast ratio
                if (colorInversionNeededInHC)
                {
                    penColor = InvertColor(OwnerGrid.LineColor);
                }
                else
                {
                    // Filling rectangle as it was in all cases where we do not invert colors.
                    Color brushColor = GetBackgroundColor();
                    using var brush = brushColor.GetCachedSolidBrushScope();
                    g.FillRectangle(brush, outline);
                }

                using var pen = penColor.GetCachedPenScope();

                g.DrawRectangle(pen, outline.X, outline.Y, outline.Width - 1, outline.Height - 1);

                // draw horizontal line for +/-
                int indent = 2;
                g.DrawLine(
                    pen,
                    outline.X + indent,
                    outline.Y + outline.Height / 2,
                    outline.X + outline.Width - indent - 1,
                    outline.Y + outline.Height / 2);

                // draw vertical line to make a +
                if (!fExpanded)
                {
                    g.DrawLine(
                        pen,
                        outline.X + outline.Width / 2,
                        outline.Y + indent,
                        outline.X + outline.Width / 2,
                        outline.Y + outline.Height - indent - 1);
                }
            }
        }

        /// <summary>
        ///  Paints the value portion of this GridEntry into the given Graphics object. This is called by the GridEntry
        ///  host (the PropertyGridView) when this GridEntry is to be painted.
        /// </summary>
        public virtual void PaintValue(object val, Graphics g, Rectangle rect, Rectangle clipRect, PaintValueFlags paintFlags)
        {
            PropertyGridView gridHost = GridEntryHost;
            Debug.Assert(gridHost is not null);

            Color textColor = ShouldRenderReadOnly ? GridEntryHost.GrayTextColor : gridHost.GetTextColor();

            string text;

            if (paintFlags.HasFlag(PaintValueFlags.FetchValue))
            {
                if (_cacheItems is not null && _cacheItems.UseValueString)
                {
                    text = _cacheItems.LastValueString;
                    val = _cacheItems.LastValue;
                }
                else
                {
                    val = PropertyValue;
                    text = GetPropertyTextValue(val);

                    if (_cacheItems is null)
                    {
                        _cacheItems = new CacheItems();
                    }

                    _cacheItems.LastValueString = text;
                    _cacheItems.UseValueString = true;
                    _cacheItems.LastValueTextWidth = -1;
                    _cacheItems.LastValueFont = null;
                    _cacheItems.LastValue = val;
                }
            }
            else
            {
                text = GetPropertyTextValue(val);
            }

            // Paint out the main rect using the appropriate brush
            Color backColor = GetBackgroundColor();

            if (paintFlags.HasFlag(PaintValueFlags.DrawSelected))
            {
                backColor = gridHost.GetSelectedItemWithFocusBackColor();
                textColor = gridHost.GetSelectedItemWithFocusForeColor();
            }

            using var backBrush = backColor.GetCachedSolidBrushScope();
            g.FillRectangle(backBrush, clipRect);

            int paintIndent = 0;
            if (IsCustomPaint)
            {
                paintIndent = gridHost.GetValuePaintIndent();
                Rectangle rectPaint = new Rectangle(
                    rect.X + 1,
                    rect.Y + 1,
                    gridHost.GetValuePaintWidth(),
                    gridHost.GetGridEntryHeight() - 2);

                if (!Rectangle.Intersect(rectPaint, clipRect).IsEmpty)
                {
                    UITypeEditor?.PaintValue(new PaintValueEventArgs(this, val, g, rectPaint));

                    // Paint a border around the area
                    rectPaint.Width--;
                    rectPaint.Height--;
                    g.DrawRectangle(SystemPens.WindowText, rectPaint);
                }
            }

            rect.X += paintIndent + gridHost.GetValueStringIndent();
            rect.Width -= paintIndent + 2 * gridHost.GetValueStringIndent();

            // Bold the property if we need to persist it (e.g. it's not the default value)
            bool valueModified = paintFlags.HasFlag(PaintValueFlags.CheckShouldSerialize) && ShouldSerializePropertyValue();

            // If we have text to paint, paint it
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (text.Length > MaximumLengthOfPropertyString)
            {
                text = text.Substring(0, MaximumLengthOfPropertyString);
            }

            int textWidth = GetValueTextWidth(text, g, GetFont(valueModified));
            bool doToolTip = false;

            // Check if text contains multiple lines
            if (textWidth >= rect.Width || GetMultipleLines(text))
            {
                doToolTip = true;
            }

            if (Rectangle.Intersect(rect, clipRect).IsEmpty)
            {
                return;
            }

            // Do actual drawing, shifting to match the PropertyGridView.GridViewListbox content alignment

            if (paintFlags.HasFlag(PaintValueFlags.PaintInPlace))
            {
                rect.Offset(1, 2);
            }
            else
            {
                // Only go down one pixel when we're painting in the listbox
                rect.Offset(1, 1);
            }

            Rectangle textRectangle = new Rectangle(
                rect.X - 1,
                rect.Y - 1,
                rect.Width - 4,
                rect.Height);

            backColor = paintFlags.HasFlag(PaintValueFlags.DrawSelected)
                ? GridEntryHost.GetSelectedItemWithFocusBackColor()
                : GridEntryHost.BackColor;

            User32.DT format = User32.DT.EDITCONTROL | User32.DT.EXPANDTABS | User32.DT.NOCLIP
                | User32.DT.SINGLELINE | User32.DT.NOPREFIX;

            if (gridHost.DrawValuesRightToLeft)
            {
                format |= User32.DT.RIGHT | User32.DT.RTLREADING;
            }

            // For password mode, replace the string value with a bullet.
            if (ShouldRenderPassword)
            {
                if (s_passwordReplaceChar == '\0')
                {
                    // Bullet is 2022, but edit box uses round circle 25CF
                    s_passwordReplaceChar = '\u25CF';
                }

                text = new string(s_passwordReplaceChar, text.Length);
            }

            TextRenderer.DrawTextInternal(
                g,
                text,
                GetFont(boldFont: valueModified),
                textRectangle,
                textColor,
                backColor,
                (TextFormatFlags)format | PropertyGrid.MeasureTextHelper.GetTextRendererFlags());

            ValueToolTipLocation = doToolTip ? new Point(rect.X + 2, rect.Y - 1) : InvalidPoint;
        }

        public virtual bool OnComponentChanging()
        {
            if (ComponentChangeService is not null)
            {
                try
                {
                    ComponentChangeService.OnComponentChanging(GetValueOwner(), PropertyDescriptor);
                }
                catch (CheckoutException coEx)
                {
                    if (coEx == CheckoutException.Canceled)
                    {
                        return false;
                    }

                    throw;
                }
            }

            return true;
        }

        public virtual void OnComponentChanged()
        {
            if (ComponentChangeService is not null)
            {
                ComponentChangeService.OnComponentChanged(GetValueOwner(), PropertyDescriptor, null, null);
            }
        }

        /// <summary>
        ///  Called when the label portion of this GridEntry is clicked.
        ///  Default implementation fired the event to any listeners, so be sure
        ///  to call base.OnLabelClick(e) if this is overridden.
        /// </summary>
        protected virtual void OnLabelClick(EventArgs e)
        {
            RaiseEvent(s_labelClickEvent, e);
        }

        /// <summary>
        ///  Called when the label portion of this GridEntry is double-clicked.
        ///  Default implementation fired the event to any listeners, so be sure
        ///  to call base.OnLabelDoubleClick(e) if this is overridden.
        /// </summary>
        protected virtual void OnLabelDoubleClick(EventArgs e)
        {
            RaiseEvent(s_labelDoubleClickEvent, e);
        }

        /// <summary>
        ///  Called when the GridEntry is clicked.
        /// </summary>
        public virtual bool OnMouseClick(int x, int y, int count, MouseButtons button)
        {
            // where are we at?
            PropertyGridView gridHost = GridEntryHost;
            Debug.Assert(gridHost is not null, "No prop entry host!");

            // make sure it's the left button
            if ((button & MouseButtons.Left) != MouseButtons.Left)
            {
                return false;
            }

            int labelWidth = gridHost.GetLabelWidth();

            // are we in the label?
            if (x >= 0 && x <= labelWidth)
            {
                // are we on the outline?
                if (Expandable)
                {
                    Rectangle outlineRect = OutlineRect;
                    if (outlineRect.Contains(x, y))
                    {
                        if (count % 2 == 0)
                        {
                            OnOutlineDoubleClick(EventArgs.Empty);
                        }
                        else
                        {
                            OnOutlineClick(EventArgs.Empty);
                        }

                        return true;
                    }
                }

                if (count % 2 == 0)
                {
                    OnLabelDoubleClick(EventArgs.Empty);
                }
                else
                {
                    OnLabelClick(EventArgs.Empty);
                }

                return true;
            }

            // are we in the value?
            labelWidth += gridHost.GetSplitterWidth();
            if (x >= labelWidth && x <= labelWidth + gridHost.GetValueWidth())
            {
                if (count % 2 == 0)
                {
                    OnValueDoubleClick(EventArgs.Empty);
                }
                else
                {
                    OnValueClick(EventArgs.Empty);
                }

                return true;
            }

            return false;
        }

        /// <summary>
        ///  Called when the outline icon portion of this GridEntry is clicked.
        ///  Default implementation fired the event to any listeners, so be sure
        ///  to call base.OnOutlineClick(e) if this is overridden.
        /// </summary>
        protected virtual void OnOutlineClick(EventArgs e)
        {
            RaiseEvent(s_outlineClickEvent, e);
        }

        /// <summary>
        ///  Called when the outline icon portion of this GridEntry is double-clicked.
        ///  Default implementation fired the event to any listeners, so be sure
        ///  to call base.OnOutlineDoubleClick(e) if this is overridden.
        /// </summary>
        protected virtual void OnOutlineDoubleClick(EventArgs e)
        {
            RaiseEvent(s_outlineDoubleClickEvent, e);
        }

        /// <summary>
        ///  Called when RecreateChildren is called.
        ///  Default implementation fired the event to any listeners, so be sure
        ///  to call base.OnOutlineDoubleClick(e) if this is overridden.
        /// </summary>
        protected virtual void OnRecreateChildren(GridEntryRecreateChildrenEventArgs e)
        {
            Delegate handler = GetEventHandler(s_recreateChildrenEvent);
            if (handler is not null)
            {
                ((GridEntryRecreateChildrenEventHandler)handler)(this, e);
            }
        }

        /// <summary>
        ///  Called when the value portion of this GridEntry is clicked.
        ///  Default implementation fired the event to any listeners, so be sure
        ///  to call base.OnValueClick(e) if this is overridden.
        /// </summary>
        protected virtual void OnValueClick(EventArgs e)
        {
            RaiseEvent(s_valueClickEvent, e);
        }

        /// <summary>
        ///  Called when the value portion of this GridEntry is clicked.
        ///  Default implementation fired the event to any listeners, so be sure
        ///  to call base.OnValueDoubleClick(e) if this is overridden.
        /// </summary>
        protected virtual void OnValueDoubleClick(EventArgs e)
        {
            RaiseEvent(s_valueDoubleClickEvent, e);
        }

        internal bool OnValueReturnKey()
        {
            return NotifyValue(NOTIFY_RETURN);
        }

        /// <summary>
        ///  Sets the specified flag
        /// </summary>
        protected virtual void SetFlag(int flag, bool fVal)
        {
            SetFlag(flag, (fVal ? flag : 0));
        }

        /// <summary>
        ///  Sets the default child of this entry, given a valid value mask.
        /// </summary>
        protected virtual void SetFlag(int flag_valid, int flag, bool fVal)
        {
            SetFlag(flag_valid | flag,
                    flag_valid | (fVal ? flag : 0));
        }

        /// <summary>
        ///  Sets the value of a flag
        /// </summary>
        protected virtual void SetFlag(int flag, int val)
        {
            Flags = (Flags & ~(flag)) | val;
        }

        public override bool Select()
        {
            if (Disposed)
            {
                return false;
            }

            try
            {
                GridEntryHost.SelectedGridEntry = this;
                return true;
            }
            catch
            {
            }

            return false;
        }

        /// <summary>
        ///  Checks if this value should be persisted.
        /// </summary>
        internal virtual bool ShouldSerializePropertyValue()
        {
            if (_cacheItems is not null)
            {
                if (_cacheItems.UseShouldSerialize)
                {
                    return _cacheItems.LastShouldSerialize;
                }
                else
                {
                    _cacheItems.LastShouldSerialize = NotifyValue(NOTIFY_SHOULD_PERSIST);
                    _cacheItems.UseShouldSerialize = true;
                }
            }
            else
            {
                _cacheItems = new CacheItems
                {
                    LastShouldSerialize = NotifyValue(NOTIFY_SHOULD_PERSIST),
                    UseShouldSerialize = true
                };
            }

            return _cacheItems.LastShouldSerialize;
        }

        private PropertyDescriptor[] SortParenProperties(PropertyDescriptor[] props)
        {
            PropertyDescriptor[] newProps = null;
            int newPos = 0;

            // first scan the list and move any parenthesized properties to the front.
            for (int i = 0; i < props.Length; i++)
            {
                if (((ParenthesizePropertyNameAttribute)props[i].Attributes[typeof(ParenthesizePropertyNameAttribute)]).NeedParenthesis)
                {
                    if (newProps is null)
                    {
                        newProps = new PropertyDescriptor[props.Length];
                    }

                    newProps[newPos++] = props[i];
                    props[i] = null;
                }
            }

            // second pass, copy any that didn't have the parens.
            if (newPos > 0)
            {
                for (int i = 0; i < props.Length; i++)
                {
                    if (props[i] is not null)
                    {
                        newProps[newPos++] = props[i];
                    }
                }

                props = newProps;
            }

            return props;
        }

        /// <summary>
        ///  Sends a notify message to this GridEntry, and returns the success result
        /// </summary>
        internal virtual bool NotifyValueGivenParent(object obj, int type)
        {
            return false;
        }

        /// <summary>
        ///  Sends a notify message to the child GridEntry, and returns the success result
        /// </summary>
        internal virtual bool NotifyChildValue(GridEntry pe, int type)
        {
            return pe.NotifyValueGivenParent(pe.GetValueOwner(), type);
        }

        internal virtual bool NotifyValue(int type)
        {
            if (_parentEntry is null)
            {
                return true;
            }
            else
            {
                return _parentEntry.NotifyChildValue(this, type);
            }
        }

        internal void RecreateChildren()
        {
            RecreateChildren(-1);
        }

        internal void RecreateChildren(int oldCount)
        {
            // cause the flags to be rebuilt as well...
            bool wasExpanded = InternalExpanded || oldCount > 0;

            if (oldCount == -1)
            {
                oldCount = VisibleChildCount;
            }

            ResetState();
            if (oldCount == 0)
            {
                return;
            }

            foreach (GridEntry child in ChildCollection)
            {
                child.RecreateChildren();
            }

            DisposeChildren();
            InternalExpanded = wasExpanded;
            OnRecreateChildren(new GridEntryRecreateChildrenEventArgs(oldCount, VisibleChildCount));
        }

        /// <summary>
        ///  Refresh the current GridEntry's value and it's children
        /// </summary>
        public virtual void Refresh()
        {
            Type type = PropertyType;
            if (type is not null && type.IsArray)
            {
                CreateChildren(true);
            }

            if (_childCollection is not null)
            {
                // check to see if the value has changed.
                //
                if (InternalExpanded && _cacheItems is not null && _cacheItems.LastValue is not null && _cacheItems.LastValue != PropertyValue)
                {
                    ClearCachedValues();
                    RecreateChildren();
                    return;
                }
                else if (InternalExpanded)
                {
                    // otherwise just do a refresh.
                    IEnumerator childEnum = _childCollection.GetEnumerator();
                    while (childEnum.MoveNext())
                    {
                        object o = childEnum.Current;
                        Debug.Assert(o is not null, "Collection contains a null element.  But how? Garbage collector hole?  GDI+ corrupting memory?");
                        GridEntry e = (GridEntry)o;
                        e.Refresh();
                    }
                }
                else
                {
                    DisposeChildren();
                }
            }

            ClearCachedValues();
        }

        public virtual void RemoveOnLabelClick(EventHandler h)
        {
            RemoveEventHandler(s_labelClickEvent, h);
        }

        public virtual void RemoveOnLabelDoubleClick(EventHandler h)
        {
            RemoveEventHandler(s_labelDoubleClickEvent, h);
        }

        public virtual void RemoveOnValueClick(EventHandler h)
        {
            RemoveEventHandler(s_valueClickEvent, h);
        }

        public virtual void RemoveOnValueDoubleClick(EventHandler h)
        {
            RemoveEventHandler(s_valueDoubleClickEvent, h);
        }

        public virtual void RemoveOnOutlineClick(EventHandler h)
        {
            RemoveEventHandler(s_outlineClickEvent, h);
        }

        public virtual void RemoveOnOutlineDoubleClick(EventHandler h)
        {
            RemoveEventHandler(s_outlineDoubleClickEvent, h);
        }

        public virtual void RemoveOnRecreateChildren(GridEntryRecreateChildrenEventHandler h)
        {
            RemoveEventHandler(s_recreateChildrenEvent, h);
        }

        protected void ResetState()
        {
            Flags = 0;
            ClearCachedValues();
        }

        /// <summary>
        ///  Sets the value of this GridEntry from text
        /// </summary>
        public virtual bool SetPropertyTextValue(string str)
        {
            bool fChildrenPrior = (_childCollection is not null && _childCollection.Count > 0);
            PropertyValue = ConvertTextToValue(str);
            CreateChildren();
            bool fChildrenAfter = (_childCollection is not null && _childCollection.Count > 0);
            return (fChildrenPrior != fChildrenAfter);
        }

        public override string ToString()
        {
            return GetType().FullName + " " + PropertyLabel;
        }

        private EventEntry eventList;

        protected virtual void AddEventHandler(object key, Delegate handler)
        {
            // Locking 'this' here is ok since this is an internal class.
            lock (this)
            {
                if (handler is null)
                {
                    return;
                }

                for (EventEntry e = eventList; e is not null; e = e.Next)
                {
                    if (e.Key == key)
                    {
                        e.Handler = Delegate.Combine(e.Handler, handler);
                        return;
                    }
                }

                eventList = new EventEntry(eventList, key, handler);
            }
        }

        protected virtual void RaiseEvent(object key, EventArgs e)
        {
            Delegate handler = GetEventHandler(key);
            if (handler is not null)
            {
                ((EventHandler)handler)(this, e);
            }
        }

        protected virtual Delegate GetEventHandler(object key)
        {
            // Locking 'this' here is ok since this is an internal class.
            lock (this)
            {
                for (EventEntry e = eventList; e is not null; e = e.Next)
                {
                    if (e.Key == key)
                    {
                        return e.Handler;
                    }
                }

                return null;
            }
        }

        protected virtual void RemoveEventHandler(object key, Delegate handler)
        {
            // Locking this here is ok since this is an internal class.
            lock (this)
            {
                if (handler is null)
                {
                    return;
                }

                for (EventEntry e = eventList, prev = null; e is not null; prev = e, e = e.Next)
                {
                    if (e.Key == key)
                    {
                        e.Handler = Delegate.Remove(e.Handler, handler);
                        if (e.Handler is null)
                        {
                            if (prev is null)
                            {
                                eventList = e.Next;
                            }
                            else
                            {
                                prev.Next = e.Next;
                            }
                        }

                        return;
                    }
                }
            }
        }

        protected virtual void RemoveEventHandlers()
        {
            eventList = null;
        }
    }

    internal delegate void GridEntryRecreateChildrenEventHandler(object sender, GridEntryRecreateChildrenEventArgs rce);
}
