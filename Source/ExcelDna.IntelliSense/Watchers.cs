using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using Point = System.Drawing.Point;

namespace ExcelDna.IntelliSense
{
    // NOTE: We're using the COM wrapper (UIAComWrapper) approach to UI Automation, rather than using the old System.Automation in .NET BCL
    //       My understanding is from this post : https://social.msdn.microsoft.com/Forums/en-US/69cf1072-d57f-4aa1-a8ea-ea8a9a5da70a/using-uiautomation-via-com-interopuiautomationclientdll-causes-windows-explorer-to-crash?forum=windowsaccessibilityandautomation
    //       I think this is a sample of the new UI Automation 3.0 COM API: http://blogs.msdn.com/b/winuiautomation/archive/2012/03/06/windows-7-ui-automation-client-api-c-sample-e-mail-reader-version-1-0.aspx
    //       Really need to keep in mind the threading guidance from here: https://msdn.microsoft.com/en-us/library/windows/desktop/ee671692(v=vs.85).aspx

    // NOTE: Check that the TextPattern is available where expected under Windows 7 (remember about CLSID_CUIAutomation8 class)

    enum FormulaEditFocus
    {
        None = 0,
        FormulaBar = 1,
        InCellEdit = 2
    }

    // All the code in this class runs on the Automation thread, including events we handle from the WinEventHook.
    class WindowWatcher : IDisposable
    {
        WinEventHook _windowStateChangeHook;

        public class WindowChangedEventArgs : EventArgs
        {
            public enum ChangeType
            {
                Create = 1,
                Destroy = 2,
                Show = 3,
                Hide = 4
            }

            public ChangeType Type;
        }

//         public IntPtr MainWindow { get; private set; }
        public IntPtr FormulaBarWindow { get; private set; }    // Move to FormulaEditWatcher ??
        public IntPtr InCellEditWindow { get; private set; }    // Move to FormulaEditWatcher ??
        public FormulaEditFocus FormulaEditFocus { get; private set; }  // Move to FormulaEditWatcher ??
        public IntPtr PopupListWindow { get; private set; }
        public IntPtr IsPopupListWindowVisible { get; private set; }
        // public IntPtr PopupListList { get; private set; }
        public IntPtr SelectDataSourceWindow { get; private set; }
        public bool IsSelectDataSourceWindowVisible { get; private set; }
        public IntPtr FormulaEditWindow  // Move to FormulaEditWatcher ??
        {
            get
            {
                switch (FormulaEditFocus)
                {
                    case FormulaEditFocus.None:
                        return IntPtr.Zero;
                    case FormulaEditFocus.InCellEdit:
                        return InCellEditWindow;
                    case FormulaEditFocus.FormulaBar:
                        return FormulaBarWindow;
                    default:
                        throw new ArgumentException("Incalid FormulaEditFocus");
                }

            }
        }

        // NOTE: The WindowWatcher raises all events on our Automation thread (via syncContextAuto passed into the constructor).
        //        public event EventHandler MainWindowChanged;
        public event EventHandler FormulaBarWindowChanged;
        public event EventHandler InCellEditWindowChanged;
        public event EventHandler FormulaEditFocusChanged;
        public event EventHandler<WindowChangedEventArgs> PopupListWindowChanged;   // Might start off with nothing. Changes at most once.?????
        public event EventHandler IsPopupListWindowsVisibleChanged;
        // public event EventHandler PopupListListChanged = delegate { };   // Might start off with nothing. Changes at most once.??????
        public event EventHandler<WindowChangedEventArgs> SelectDataSourceWindowChanged;
        public event EventHandler IsSelectDataSourceWindowVisibleChanged;

        public WindowWatcher(SynchronizationContext syncContextAuto)
        {
            #pragma warning disable CS0618 // Type or member is obsolete (GetCurrentThreadId) - But for debugging we want to monitor this anyway
            // Debug.Print($"### WindowWatcher created on thread: Managed {Thread.CurrentThread.ManagedThreadId}, Native {AppDomain.GetCurrentThreadId()}");
            #pragma warning restore CS0618 // Type or member is obsolete

            // Using WinEvents instead of Automation so that we can watch top-level window changes, but only from the right (current Excel) process.
            // TODO: We need to dramatically reduce the number of events we grab here...
            _windowStateChangeHook = new WinEventHook(WinEventHook.WinEvent.EVENT_OBJECT_CREATE, WinEventHook.WinEvent.EVENT_OBJECT_FOCUS, syncContextAuto);
            _windowStateChangeHook.WinEventReceived += _windowStateChangeHook_WinEventReceived;
        }

        // Runs on the Automation thread (before syncContextAuto starts pumping)
        public void TryInitialize()
        {
            // Debug.Print("### WindowWatcher TryInitialize on thread: " + Thread.CurrentThread.ManagedThreadId);
            AutomationElement focused;
            try
            {
                focused = AutomationElement.FocusedElement;
                var className = focused.GetCurrentPropertyValue(AutomationElement.ClassNameProperty);
                Debug.Print($"WindowWatcher.TryInitialize - Focused: {className}");
            }
            catch (ArgumentException aex)
            {
                Debug.Print($"!!! ERROR: Failed to get Focused Element: {aex}");
                return;
            }

            //// Start at the focused control, search ancestors until we find the main window
            //AutomationElement current = focused;
            //TreeWalker treeWalker = TreeWalker.ControlViewWalker;
            //while ((string)current.GetCurrentPropertyValue(AutomationElement.ClassNameProperty) != _mainWindowClass)
            //{
            //    // Debug.Print($"Current Class = {current.GetCurrentPropertyValue(AutomationElement.ClassNameProperty)}");
            //    current = treeWalker.GetParent(current);
            //    if (current == null)
            //    {
            //        Debug.Print("!!! WARNING: Failed to get main window from focused element"); // We'll be OK when a main window gets the focus
            //        return;    // At the root
            //    }
            //}
            //// ... and update
            //UpdateMainWindow((IntPtr)(int)current.GetCurrentPropertyValue(AutomationElement.NativeWindowHandleProperty));
        }

        // This runs on the Automation thread, via SyncContextAuto passed in to WinEventHook when we created this WindowWatcher
        // CONSIDER: We might be able to run all the watcher updates from WinEvents, including Location and Selection changes.
        void _windowStateChangeHook_WinEventReceived(object sender, WinEventHook.WinEventArgs e)
        {
            // Debug.Print("### Thread receiving WindowStateChange: " + Thread.CurrentThread.ManagedThreadId);
            var className = Win32Helper.GetClassName(e.WindowHandle);
            switch (className)
            {
                //case _mainWindowClass:
                //    if (e.EventType == WinEventHook.WinEvent.EVENT_OBJECT_CREATE || 
                //        e.EventType == WinEventHook.WinEvent.EVENT_OBJECT_DESTROY ||
                //        e.EventType == WinEventHook.WinEvent.EVENT_OBJECT_SHOW ||
                //        e.EventType == WinEventHook.WinEvent.EVENT_OBJECT_HIDE ||
                //        e.EventType == WinEventHook.WinEvent.EVENT_OBJECT_REORDER ||
                //        e.EventType == WinEventHook.WinEvent.EVENT_OBJECT_FOCUS)
                //    {
                //        // Debug.Print($"MainWindow update: {e.WindowHandle:X}, EventType: {e.EventType}");
                //        UpdateMainWindow(e.WindowHandle);
                //    }
                //    break;
                case _sheetWindowClass:
                    if (e.EventType == WinEventHook.WinEvent.EVENT_OBJECT_SHOW)
                    {
                        // Maybe a new workbook is on top...
                        // Note that there is also an EVENT_OBJECT_PARENTCHANGE (which we are not subscribing to at the moment
                    }
                    break;
                case _popupListClass:
                    if (e.EventType == WinEventHook.WinEvent.EVENT_OBJECT_CREATE ||
                        e.EventType == WinEventHook.WinEvent.EVENT_OBJECT_SHOW)   // SHOW also since we might be installed later...
                    {
                        // Debug.Assert(PopupListWindow == IntPtr.Zero || e.EventType == WinEventHook.WinEvent.EVENT_OBJECT_SHOW, "Unexpected new PopupList window...????");
                        PopupListWindow = e.WindowHandle;
                        PopupListWindowChanged?.Invoke(this, 
                            new WindowChangedEventArgs { Type = 
                                (e.EventType == WinEventHook.WinEvent.EVENT_OBJECT_CREATE) ? 
                                    WindowChangedEventArgs.ChangeType.Create : 
                                    WindowChangedEventArgs.ChangeType.Show });
                    }
                    else if (PopupListWindow != IntPtr.Zero && e.EventType == WinEventHook.WinEvent.EVENT_OBJECT_HIDE)
                    {
                        PopupListWindowChanged?.Invoke(this, new WindowChangedEventArgs { Type = WindowChangedEventArgs.ChangeType.Hide });
                    }
                    else if (PopupListWindow != IntPtr.Zero && e.EventType == WinEventHook.WinEvent.EVENT_OBJECT_DESTROY)
                    {
                        // Expected when shutting down
                        PopupListWindowChanged?.Invoke(this, new WindowChangedEventArgs { Type = WindowChangedEventArgs.ChangeType.Destroy });
                    }
                    break;
                case _inCellEditClass:
                    // Debug.Print($"InCell Window update: {e.WindowHandle:X}, EventType: {e.EventType}, idChild: {e.ChildId}");
                    if (e.EventType == WinEventHook.WinEvent.EVENT_OBJECT_CREATE ||
                         e.EventType == WinEventHook.WinEvent.EVENT_OBJECT_SHOW) // SHOW also since we might be installed later...
                    {
                        if (InCellEditWindow != e.WindowHandle)
                        {
                            InCellEditWindow = e.WindowHandle;
                            InCellEditWindowChanged?.Invoke(this, EventArgs.Empty);
                        }
                    }
                    else if (e.EventType == WinEventHook.WinEvent.EVENT_OBJECT_HIDE)
                    {
                        FormulaEditFocus = FormulaEditFocus.None;
                        FormulaEditFocusChanged?.Invoke(this, EventArgs.Empty);
                    }
                    else if (e.EventType == WinEventHook.WinEvent.EVENT_OBJECT_FOCUS)
                    {
                        FormulaEditFocus = FormulaEditFocus.InCellEdit;
                        FormulaEditFocusChanged?.Invoke(this, EventArgs.Empty);
                    }
                    break;
                case _formulaBarClass:
                    // Debug.Print($"FormulaBar Window update: {e.WindowHandle:X}, EventType: {e.EventType}, idChild: {e.ChildId}");
                    if (e.EventType == WinEventHook.WinEvent.EVENT_OBJECT_CREATE ||
                        e.EventType == WinEventHook.WinEvent.EVENT_OBJECT_SHOW)
                    {
                        if (FormulaBarWindow != e.WindowHandle)
                        {
                            FormulaBarWindow = e.WindowHandle;
                            FormulaBarWindowChanged?.Invoke(this, EventArgs.Empty);
                        }
                    }
                    else if (e.EventType == WinEventHook.WinEvent.EVENT_OBJECT_FOCUS)
                    {
                        FormulaEditFocus = FormulaEditFocus.FormulaBar;
                        FormulaEditFocusChanged?.Invoke(this, EventArgs.Empty);
                    }
                    break;
                case _selectDataSourceClass:
                    // Debug.Print($"SelectDataSource {_selectDataSourceClass} Window update: {e.WindowHandle:X}, EventType: {e.EventType}, idChild: {e.ChildId}");
                    if (e.EventType == WinEventHook.WinEvent.EVENT_OBJECT_CREATE)
                    {
                        // Get the name of this window - maybe ours or some other NUIDialog
                        var windowTitle = Win32Helper.GetText(e.WindowHandle);
                        if (windowTitle.Equals(_selectDataSourceTitle, StringComparison.OrdinalIgnoreCase))
                        {
                            SelectDataSourceWindow = e.WindowHandle;
                            SelectDataSourceWindowChanged?.Invoke(this, 
                                new WindowChangedEventArgs { Type = WindowChangedEventArgs.ChangeType.Create });
                        }
                    }
                    else if (SelectDataSourceWindow == e.WindowHandle && e.EventType == WinEventHook.WinEvent.EVENT_OBJECT_SHOW)
                    {
                        IsSelectDataSourceWindowVisible = true;
                        SelectDataSourceWindowChanged?.Invoke(this,
                                new WindowChangedEventArgs { Type = WindowChangedEventArgs.ChangeType.Create });
                    }
                    else if (SelectDataSourceWindow == e.WindowHandle && e.EventType == WinEventHook.WinEvent.EVENT_OBJECT_HIDE)
                    {
                        IsSelectDataSourceWindowVisible = false;
                        SelectDataSourceWindowChanged?.Invoke(this, new WindowChangedEventArgs { Type = WindowChangedEventArgs.ChangeType.Hide });
                    }
                    else if (SelectDataSourceWindow == e.WindowHandle && e.EventType == WinEventHook.WinEvent.EVENT_OBJECT_DESTROY)
                    {
                        IsSelectDataSourceWindowVisible = false;
                        SelectDataSourceWindow = IntPtr.Zero;
                        SelectDataSourceWindowChanged?.Invoke(this, new WindowChangedEventArgs { Type = WindowChangedEventArgs.ChangeType.Destroy });
                    }
                    break;
                default:
                    //InCellEditWindowChanged(this, EventArgs.Empty);
                    break;
            }
        }

//        // Runs on our automation thread
//        void UpdateMainWindow(IntPtr hWnd)
//        {
//            if (MainWindow != hWnd)
//            {
//                MainWindow = hWnd;
//                FormulaBarWindow = IntPtr.Zero;
//                MainWindowChanged?.Invoke(this, EventArgs.Empty);
//            }

//            if (FormulaBarWindow == IntPtr.Zero)
//            {
//                // Either fresh window, or children not yet set up

//                //try // Anything can go wrong here, e.g. we're shutting down on the main thread
//                //{

//                    // Search for formulaBar

//                    AutomationElement mainWindow = AutomationElement.FromHandle(hWnd);
//#if DEBUG
//                    var mainChildren = mainWindow.FindAll(TreeScope.Children, Condition.TrueCondition);
//                    foreach (AutomationElement child in mainChildren)
//                    {
//                        var hWndChild = (IntPtr)(int)child.GetCurrentPropertyValue(AutomationElement.NativeWindowHandleProperty);
//                        var classChild = (string)child.GetCurrentPropertyValue(AutomationElement.ClassNameProperty);
//                        // Debug.Print($"Child: {hWndChild}, Class: {classChild}");
//                    }
//#endif
//                    AutomationElement formulaBar = mainWindow.FindFirst(TreeScope.Children,
//                        new PropertyCondition(AutomationElement.ClassNameProperty, _formulaBarClass));
//                    if (formulaBar != null)
//                    {
//                        FormulaBarWindow = (IntPtr)(int)formulaBar.GetCurrentPropertyValue(AutomationElement.NativeWindowHandleProperty);

//                        // CONSIDER:
//                        //  Watch WindowClose event for MainWindow?

//                        FormulaBarWindowChanged?.Invoke(this, EventArgs.Empty);
//                    }
//                    else
//                    {
//                        // Debug.Print("Could not get FormulaBar!");
//                    }
//                //}
//                //catch (Exception ex)
//                //{

//                //}
//            }
//        }

        const string _mainWindowClass = "XLMAIN";
        const string _sheetWindowClass = "EXCEL7";  // This is the sheet portion (not top level) - we get some notifications from here?
        const string _formulaBarClass = "EXCEL<";
        const string _inCellEditClass = "EXCEL6";
        const string _popupListClass = "__XLACOOUTER";
        const string _selectDataSourceClass = "NUIDialog";
        const string _selectDataSourceTitle = "Select Data Source";     // TODO: How does localization work?

        public void Dispose()
        {
            if (_windowStateChangeHook != null)
            {
                _windowStateChangeHook.Dispose();
                _windowStateChangeHook = null;
            }
        }
    }

    // We want to know whether to show the function entry help
    // For now we ignore whether another ToolTip is being shown, and just use the formula edit state.
    // CONSIDER: Should we watch the in-cell edit box and the formula edit control separately?
    class FormulaEditWatcher : IDisposable
    {
        public enum StateChangeType
        {
            Undefined,
            Move,
            TextChangedOnly
        }

        public class StateChangeEventArgs : EventArgs
        {
            public static new StateChangeEventArgs Empty = new StateChangeEventArgs();

            public StateChangeType StateChangeType { get; private set; }

            public StateChangeEventArgs(StateChangeType? stateChangeType = null)
            {
                StateChangeType = stateChangeType ?? StateChangeType.Undefined;
            }
        }

        IntPtr            _hwndFormulaBar;
        AutomationElement _formulaBar;
        AutomationElement _inCellEdit;
        FormulaEditFocus _formulaEditFocus;

        // NOTE: Our event will always be raised on the _syncContextAuto thread (CONSIDER: Does this help?)
        public event EventHandler<StateChangeEventArgs> StateChanged;

        public bool IsEditingFormula { get; set; }
        //        public string CurrentFormula { get; set; } // Easy to get, but we don't need it
        public string CurrentPrefix { get; set; }    // Null if not editing
        // We don't really care whether it is the formula bar or in-cell, 
        // we just need to get the right window handle 
        public Rect EditWindowBounds { get; set; }
        public Point CaretPosition { get; set; }
        public IntPtr FormulaBarWindow => _hwndFormulaBar;

        SynchronizationContext _syncContextAuto;
        WindowWatcher _windowWatcher;

        public FormulaEditWatcher(WindowWatcher windowWatcher, SynchronizationContext syncContextAuto)
        {
            _syncContextAuto = syncContextAuto;
            _windowWatcher = windowWatcher;
            _windowWatcher.FormulaBarWindowChanged += _windowWatcher_FormulaBarWindowChanged;
            _windowWatcher.InCellEditWindowChanged += _windowWatcher_InCellEditWindowChanged;
            _windowWatcher.FormulaEditFocusChanged += _windowWatcher_FormulaEditFocusChanged;
//            _windowWatcher.MainWindowChanged += _windowWatcher_MainWindowChanged;
        }

        //// Runs on the Automation thread
        //void _windowWatcher_MainWindowChanged(object sender, EventArgs args)
        //{
        //    if (_mainWindow != null)
        //    {
        //        Automation.RemoveAutomationPropertyChangedEventHandler(_mainWindow, LocationChanged);
        //    }

        //    WindowWatcher windowWatcher = (WindowWatcher)sender;

        //    if (windowWatcher.MainWindow != IntPtr.Zero)
        //    {
        //        // TODO: I see "Element not found" errors here...
        //        //       There are different top-level windows in Excl 2013.
        //        // _mainWindow = AutomationElement.FromHandle(windowWatcher.MainWindow);
        //        // Automation.AddAutomationPropertyChangedEventHandler(_mainWindow, TreeScope.Element, LocationChanged, AutomationElement.BoundingRectangleProperty);
        //    }
        //}

        // Runs on the Automation thread
        void _windowWatcher_FormulaBarWindowChanged(object sender, EventArgs e)
        {
            var hWnd = _windowWatcher.FormulaBarWindow;
            // Debug.Print($"Will be watching Formula Bar: {hWnd}");
            if (_formulaBar != null)
            {
                // TODO: I've seen ElementNotAvailable here...
                // IntPtr hwndFormulaBar = (IntPtr)(int)_formulaBar.GetCurrentPropertyValue(AutomationElement.NativeWindowHandleProperty);
                if (_hwndFormulaBar == hWnd)
                {
                    // Window is fine - might be a text update
                    // Debug.Print("Doing Update for FormulaBar");

                    UpdateEditState();
                    UpdateFormula();
                    return;
                }
                // TODO: Clean up
                Automation.RemoveAutomationEventHandler(TextPattern.TextChangedEvent, _formulaBar, TextChanged);
                _formulaBar = null;
            }
            _hwndFormulaBar = hWnd;
            _formulaBar = AutomationElement.FromHandle(hWnd);
            UpdateEditState();

            // CONSIDER: What when the formula resizes?
            Automation.AddAutomationEventHandler(TextPattern.TextChangedEvent, _formulaBar, TreeScope.Element, TextChanged);
        }

        // Runs on the Automation thread
        void _windowWatcher_InCellEditWindowChanged(object sender, EventArgs e)
        {
            var hWnd = _windowWatcher.InCellEditWindow;
            if (_inCellEdit != null)
            {
                // TODO: This is not a safe call if it has been destroyed
                object objInCellHandle = _inCellEdit.GetCurrentPropertyValue(AutomationElement.NativeWindowHandleProperty);
                if (objInCellHandle != null && hWnd == (IntPtr)(int)objInCellHandle)
                {
                    // Window is fine - might be a text update
                    // Debug.Print("Doing Update for InCellEdit");
                    UpdateEditState();
                    UpdateFormula();
                    return;
                }

                //// Change is upon us,
                //// out with the old...
                //Automation.RemoveAutomationEventHandler(TextPattern.TextChangedEvent, _inCellEdit, TextChanged);

                _inCellEdit = null;
            }

            if (hWnd != IntPtr.Zero)
            {
                _inCellEdit = AutomationElement.FromHandle(hWnd);
                UpdateEditState();
                //// CONSIDER: Can the incell box resize?
                Automation.AddAutomationEventHandler(TextPattern.TextChangedEvent, _inCellEdit, TreeScope.Element, TextChanged);
            }
            else
            {
                UpdateEditState();
            }
        }

        // Runs on an Automation event thread
        // CONSIDER: With WinEvents we could get notified from main thread
        void TextChanged(object sender, AutomationEventArgs e)
        {
            // Debug.Print($">>>> FormulaEditWatcher.TextChanged on thread {Thread.CurrentThread.ManagedThreadId}");
            // Debug.WriteLine($"! Active Text text changed. Is it the Formula Bar? {sender.Equals(_formulaBar)}, Is it the In Cell Edit? {sender.Equals(_inCellEdit)}");
            UpdateFormula(textChangedOnly: true);
        }

        // Runs on an Automation event thread
        // CONSIDER: With WinEvents we could get notified from main thread
        void LocationChanged(object sender, AutomationPropertyChangedEventArgs e)
        {
            // Debug.Print($">>>> FormulaEditWatcher.LocationChanged on thread {Thread.CurrentThread.ManagedThreadId}");
            UpdateEditState(true);
        }

        // Runs on the Automation thread
        void _windowWatcher_FormulaEditFocusChanged(object sender, EventArgs e)
        {
            UpdateEditState();
            UpdateFormula();
        }

        void UpdateEditState(bool moveOnly = false)
        {
            Debug.Print("> FormulaEditWatcher.UpdateEditState");
            // Needs 
            _syncContextAuto.Post(delegate
                {
                    // TODO: This is not right yet - the list box might have the focus... ?
                    AutomationElement focused;
                    try
                    {
                        focused = AutomationElement.FocusedElement;
                    }
                    catch (ArgumentException aex)
                    {
                        Debug.Print($"!!! ERROR: Failed to get Focused Element: {aex}");
                        // Not sure why I get this - sometimes with startup screen
                        return;
                    }
                    if (_formulaBar != null && _formulaBar.Equals(focused))
                    {
                        EditWindowBounds = (Rect)_formulaBar.GetCurrentPropertyValue(AutomationElement.BoundingRectangleProperty);
                        IntPtr hwnd = (IntPtr)(int)_formulaBar.GetCurrentPropertyValue(AutomationElement.NativeWindowHandleProperty);
                        var pt = Win32Helper.GetClientCursorPos(hwnd);
                        CaretPosition = new Point(pt.X, pt.Y);
                    }
                    else if (_inCellEdit != null && _inCellEdit.Equals(focused))
                    {
                        EditWindowBounds = (Rect)_inCellEdit.GetCurrentPropertyValue(AutomationElement.BoundingRectangleProperty);
                        IntPtr hwnd = (IntPtr)(int)_inCellEdit.GetCurrentPropertyValue(AutomationElement.NativeWindowHandleProperty);
                        var pt = Win32Helper.GetClientCursorPos(hwnd);
                        CaretPosition = new Point(pt.X, pt.Y);
                    }
                    else
                    {
                        // Neither have the focus, so we don't update anything
                        // CurrentFormula = null;
                        // CurrentPrefix = null;
                        // Debug.Print("Don't have a focused text box to update.");
                    }

                    // As long as we have an InCellEdit, we are editing the formula...
                    IsEditingFormula = (_inCellEdit != null);

                    // TODO: Smarter notification...?
                    OnStateChanged(new StateChangeEventArgs(moveOnly ? StateChangeType.Move : StateChangeType.Undefined));
                }, null);
        }

        void UpdateFormula(bool textChangedOnly = false)
        {
            // Debug.Print($">>>> FormulaEditWatcher.UpdateFormula on thread {Thread.CurrentThread.ManagedThreadId}");
            CurrentPrefix = XlCall.GetFormulaEditPrefix();  // What thread do we have to use here ...?
            OnStateChanged(textChangedOnly ? new StateChangeEventArgs(StateChangeType.TextChangedOnly) : StateChangeEventArgs.Empty);
        }

        // We ensure that our event is raised on the Automation thread .. (Eases concurrency issues in handling it, though it will get passed on to the main thread...)
        void OnStateChanged(StateChangeEventArgs stateChangeEventArgs)
        {
            _syncContextAuto.Post(args => StateChanged?.Invoke(this, (StateChangeEventArgs)args), stateChangeEventArgs);
        }

        public void Dispose()
        {
            // Not sure we need this:
            _windowWatcher.FormulaBarWindowChanged -= _windowWatcher_FormulaBarWindowChanged;
            _windowWatcher.InCellEditWindowChanged -= _windowWatcher_InCellEditWindowChanged;
            _windowWatcher.FormulaEditFocusChanged -= _windowWatcher_FormulaEditFocusChanged;
//            _windowWatcher.MainWindowChanged -= _windowWatcher_MainWindowChanged;

            _syncContextAuto.Post(delegate 
            {
                Debug.Print("Disposing FormulaEditWatcher");
                if (_formulaBar != null)
                {
                    Automation.RemoveAutomationEventHandler(TextPattern.TextChangedEvent, _formulaBar, TextChanged);
                    _formulaBar = null;
                }
                //if (_inCellEdit != null)
                //{
                //    Automation.RemoveAutomationEventHandler(TextPattern.TextChangedEvent, _inCellEdit, TextChanged);
                //    _inCellEdit = null;
                //}
                //if (_mainWindow != null)
                //{
                //    Automation.RemoveAutomationPropertyChangedEventHandler(_mainWindow, LocationChanged);
                //    _mainWindow = null;
                //}
            }, null);
        }
    }

    // The Popuplist is shown both for function selection, and for some argument selection lists (e.g. TRUE/FALSE).
    // We ignore the reason for showing, and match purely on the text of the selected item.
    class PopupListWatcher : IDisposable
    {
        AutomationElement _mainWindow;
        IntPtr            _hwndPopupList;
        AutomationElement _popupList;
        AutomationElement _popupListList;
        AutomationElement _selectedItem;

        // NOTE: Event will always be raised on our automation thread
        public event EventHandler SelectedItemChanged;  // Either text or location

        public bool IsVisible{ get; set; } = false;
        public string SelectedItemText { get; set; } = string.Empty;
        public Rect SelectedItemBounds { get; set; } = Rect.Empty;
        public IntPtr PopupListHandle => _hwndPopupList;

        SynchronizationContext _syncContextAuto;
        WindowWatcher _windowWatcher;

        public PopupListWatcher(WindowWatcher windowWatcher, SynchronizationContext syncContextAuto)
        {
            _syncContextAuto = syncContextAuto;
            _windowWatcher = windowWatcher;
//            _windowWatcher.MainWindowChanged += _windowWatcher_MainWindowChanged;
            _windowWatcher.PopupListWindowChanged += _windowWatcher_PopupListWindowChanged;
        }

        // Runs on our automation thread
        void _windowWatcher_PopupListWindowChanged(object sender, WindowWatcher.WindowChangedEventArgs e)
        {
            switch (e.Type)
            {
                case WindowWatcher.WindowChangedEventArgs.ChangeType.Create:
                    // TODO: Confirm that this runs at most once
                    var hWnd = _windowWatcher.PopupListWindow;
                    // Debug.Print($">>>> PopupListWatcher - PopupListWindowChanged - New window {hWnd}");
                    Debug.Assert(_popupList == null, "PopupList window created more than once ...???");

                    _hwndPopupList = hWnd;
                    _popupList = AutomationElement.FromHandle(hWnd);
                    // We set up the structure changed handler so that we can catch the sub-list creation
                    Automation.AddAutomationPropertyChangedEventHandler(_popupList, TreeScope.Element, PopupListBoundsChanged, AutomationElement.BoundingRectangleProperty);
                    Automation.AddStructureChangedEventHandler(_popupList, TreeScope.Element, PopupListStructureChangedHandler);
                    // Automation.AddAutomationPropertyChangedEventHandler(_popupList, TreeScope.Element, PopupListVisibleChangedHandler, AutomationElement.???Visible);
                    break;
                case WindowWatcher.WindowChangedEventArgs.ChangeType.Destroy:
                    try
                    {

                        Automation.RemoveAutomationPropertyChangedEventHandler(_popupList, PopupListBoundsChanged);
                    }
                    catch (Exception ex)
                    {
                        /// Too Late????
                        Debug.Print($"PopupList Event Handler Remove Error: {ex.Message}");
                    }
                    // Expected when closing
                    // Debug.Assert(false, "PopupList window destroyed...???");
                    break;
                case WindowWatcher.WindowChangedEventArgs.ChangeType.Show:
                    IsVisible = true;
                    break;
                case WindowWatcher.WindowChangedEventArgs.ChangeType.Hide:
                    IsVisible = false;
                    UpdateSelectedItem(_selectedItem);
                    break;
                default:
                    break;
            }
        }

        //// Runs on our automation thread
        //void _windowWatcher_MainWindowChanged(object sender, EventArgs args)
        //{
        //    if (_mainWindow != null)
        //    {
        //        Automation.RemoveAutomationPropertyChangedEventHandler(_mainWindow, PopupListBoundsChanged);
        //    }

        //    WindowWatcher windowWatcher = (WindowWatcher)sender;

        //    if (windowWatcher.MainWindow != IntPtr.Zero)
        //    {
        //        // TODO: I've seen an (ElementNotAvailableException) error here that 'the element is not available'.
        //        // TODO: Lots of time-outs here when debugging, but it's probably OK...
        //        try
        //        {
        //            _mainWindow = AutomationElement.FromHandle(windowWatcher.MainWindow);
        //            Automation.AddAutomationPropertyChangedEventHandler(_mainWindow, TreeScope.Element, PopupListBoundsChanged, AutomationElement.BoundingRectangleProperty);
        //        }
        //        catch (Exception ex)
        //        {
        //            Debug.Print($"!!! Error gettting main window from handle: {ex.Message}");
        //        }
        //    }
        //}

        // This runs on an automation event handler thread
        void PopupListBoundsChanged(object sender, AutomationPropertyChangedEventArgs e)
        {
            if (IsVisible && _selectedItem != null)
            {
                // Debug.Print($">>>> PopupListWatcher.LocationChanged on thread {Thread.CurrentThread.ManagedThreadId}");
                UpdateSelectedItem(_selectedItem);
            }

            //_syncContextAuto.Post(delegate
            //{
            //    if (_popupListList != null)
            //    {
            //        //////////////////////
            //        // TODO: What is going on here ...???
            //        //////////////////////
            //        //Automation.AddAutomationEventHandler(
            //        //    SelectionItemPattern.ElementSelectedEvent, _popupListList, TreeScope.Descendants /* was .Children */, PopupListElementSelectedHandler);


            //        var selPat = _popupListList.GetCurrentPattern(SelectionPattern.Pattern) as SelectionPattern;

            //        // Update the current selection, if any
            //        var curSel = selPat.Current.GetSelection();
            //        if (curSel.Length > 0)
            //        {
            //            try
            //            {
            //                UpdateSelectedItem(curSel[0]);
            //            }
            //            catch (Exception ex)
            //            {
            //                Debug.Print("Error during UpdateSelected! " + ex);
            //            }
            //        }
            //    }
            //}, null);
        }

        // Runs on an automation event thread
        void PopupListElementSelectedHandler(object sender, AutomationEventArgs e)
        {
            // Debug.Print($">>>> PopupListWatcher.PopupListElementSelectedHandler on thread {Thread.CurrentThread.ManagedThreadId}");
            UpdateSelectedItem(sender as AutomationElement);
        }

        // TODO: This should be exposed as an event and popup resize should be elsewhere
        // Runs on an automation event thread
        void PopupListStructureChangedHandler(object sender, StructureChangedEventArgs e)
        {
            // Debug.Print($">>>> PopupListWatcher.PopupListStructureChangedHandler ({e.StructureChangeType}) on thread {Thread.CurrentThread.ManagedThreadId}");
            // Debug.WriteLine($">>> PopupList structure changed - {e.StructureChangeType}");
            // CONSIDER: Others too?
            if (e.StructureChangeType == StructureChangeType.ChildAdded)
            {
                var functionList = sender as AutomationElement;

                var listRect = (Rect)functionList.GetCurrentPropertyValue(AutomationElement.BoundingRectangleProperty);

                var listElement = functionList.FindFirst(TreeScope.Children, Condition.TrueCondition);
                if (listElement != null)
                {
                    // Debug.Print($">>>> PopupListWatcher.PopupListStructureChangedHandler Post - Children Found !!!");

                    _popupListList = listElement;

                    // TODO: Move this code!
                    // TestMoveWindow(_popupListList, (int)listRect.X, (int)listRect.Y);
                    // TestMoveWindow(functionList, 0, 0);

                    var selPat = listElement.GetCurrentPattern(SelectionPattern.Pattern) as SelectionPattern;
                    Debug.Assert(selPat != null);

                    // CONSIDER: Send might be a bit disruptive here / might not be necessary...
                    // _syncContextAuto.Send( _ =>
                    //{
                        try
                        {
                            Automation.AddAutomationEventHandler(
                                SelectionItemPattern.ElementSelectedEvent, _popupListList, TreeScope.Descendants /* was .Children */, PopupListElementSelectedHandler);
                        }
                        catch (Exception ex)
                        {
                            Debug.Print("Error during AddAutomationEventHandler! " + ex);
                        }
                    //}, null);

                    // Update the current selection, if any
                    var curSel = selPat.Current.GetSelection();
                    if (curSel.Length > 0)
                    {
                        try
                        {
                            UpdateSelectedItem(curSel[0]);
                        }
                        catch (Exception ex)
                        {
                            Debug.Print("Error during UpdateSelected! " + ex);
                        }
                    }
                }
                else
                {
                    Debug.Print($">>>> PopupListWatcher.PopupListStructureChangedHandler Post - No Children Found ??? ");
                    Debug.WriteLine("ERROR!!! Structure changed but no children anymore.");
                }
            }
            else if (e.StructureChangeType == StructureChangeType.ChildRemoved)
            {
                if (_popupListList != null)
                {
                    // CONSIDER: Send might be a bit disruptive here / might not be necessary...
                    //_syncContextAuto.Send( _ =>
                    //{
                        try
                        {
                            Automation.RemoveAutomationEventHandler(SelectionItemPattern.ElementSelectedEvent, _popupListList, PopupListElementSelectedHandler);
                        }
                        catch (Exception ex)
                        {
                            Debug.Print("Error during RemoveAutomationEventHandler! " + ex);
                        }
                    //}, null);
                    _popupListList = null;
                }
                _selectedItem = null;
                OnSelectedItemChanged();
            }
        }

        // CONSIDER: This will run on our automation thread
        //           Should be OK to call MoveWindow from there - it just posts messages to the window.
        void TestMoveWindow(AutomationElement listWindow, int xOffset, int yOffset)
        {
            var hwndList = (IntPtr)(int)(listWindow.GetCurrentPropertyValue(AutomationElement.NativeWindowHandleProperty));
            var listRect = (Rect)listWindow.GetCurrentPropertyValue(AutomationElement.BoundingRectangleProperty);
            Debug.Print("Moving from {0}, {1}", listRect.X, listRect.Y);
            Win32Helper.MoveWindow(hwndList, (int)listRect.X - xOffset, (int)listRect.Y - yOffset, (int)listRect.Width, 2 * (int)listRect.Height, true);
        }

        // Can run on our automation thread or on any automation event thread (which is also allowed to read properties)
        // But might fail, if the newSelectedItem is already gone by the time we run...
        void UpdateSelectedItem(AutomationElement newSelectedItem)
        {
            if (!IsVisible || newSelectedItem == null)
            {
                if (_selectedItem == null &&
                    SelectedItemText == string.Empty &&
                    SelectedItemBounds == Rect.Empty)
                {
                    // Don't change and fire event
                    return;
                }
                _selectedItem = null;
                SelectedItemText = string.Empty;
                SelectedItemBounds = Rect.Empty;
            }
            else
            {
                string selectedItemText = string.Empty;
                Rect selectedItemBounds = Rect.Empty;

                try
                {
                    selectedItemText = (string)newSelectedItem.GetCurrentPropertyValue(AutomationElement.NameProperty);
                    selectedItemBounds = (Rect)newSelectedItem.GetCurrentPropertyValue(AutomationElement.BoundingRectangleProperty);
                }
                catch (Exception ex)
                {
                    Debug.Print($"!!!Could not update selected item: {ex.Message}");
                    // Don't fire the event - we couldn't process this change
                    return;
                }

                _selectedItem = newSelectedItem;
                SelectedItemText = selectedItemText;
                SelectedItemBounds = selectedItemBounds;
                // Debug.Print($"SelectedItemBounds: {SelectedItemBounds}");
            }
            OnSelectedItemChanged();
        }

        // Raises the event on the automation thread (but the SyncContext.Post here is redundant)
        void OnSelectedItemChanged()
        {
            _syncContextAuto.Post(_ => SelectedItemChanged(this, EventArgs.Empty), null);
        }

        public void Dispose()
        {
            Debug.Print("Disposing PopupListWatcher");
            // CONSIDER: Do we need this?
//            _windowWatcher.MainWindowChanged -= _windowWatcher_MainWindowChanged;
            _windowWatcher.PopupListWindowChanged -= _windowWatcher_PopupListWindowChanged;
            _windowWatcher = null;

            _syncContextAuto.Post(delegate
            {
                Debug.Print("Disposing PopupListWatcher - In Automation context");
                if (_popupList != null)
                {
                    if (_popupListList != null)
                    {
                        Automation.RemoveAutomationEventHandler(SelectionItemPattern.ElementSelectedEvent, _popupListList, PopupListElementSelectedHandler);
                        _popupListList = null;
                    }

                    Automation.RemoveStructureChangedEventHandler(_popupList, PopupListStructureChangedHandler);
                    _popupList = null;
                }
            }, null);
        }
    }

    //class SelectDataSourceWatcher : IDisposable
    //{
    //    SynchronizationContext _syncContextAuto;
    //    WindowWatcher _windowWatcher;

    //    public IntPtr SelectDataSourceWindow { get; private set; }
    //    public event EventHandler SelectDataSourceWindowChanged;
    //    public bool IsVisible { get; private set; }
    //    public event EventHandler IsVisibleChanged;

    //    public SelectDataSourceWatcher(WindowWatcher windowWatcher, SynchronizationContext syncContextAuto)
    //    {
    //        _syncContextAuto = syncContextAuto;
    //        _windowWatcher = windowWatcher;
    //        _windowWatcher.SelectDataSourceWindowChanged += _windowWatcher_SelectDataSourceWindowChanged;
    //        //_windowWatcher.MainWindowChanged += _windowWatcher_MainWindowChanged;
    //        //_windowWatcher.PopupListWindowChanged += _windowWatcher_PopupListWindowChanged;
    //    }

    //    void _windowWatcher_SelectDataSourceWindowChanged(object sender, WindowWatcher.WindowChangedEventArgs e)
    //    {
    //    }

    //    public void Dispose()
    //    {
    //        Debug.Print("Disposing SelectDataSourceWatcher");
    //        // CONSIDER: Do we need this?
    //        //_windowWatcher.MainWindowChanged -= _windowWatcher_MainWindowChanged;
    //        //_windowWatcher.PopupListWindowChanged -= _windowWatcher_PopupListWindowChanged;
    //        _windowWatcher.SelectDataSourceWindowChanged -= _windowWatcher_SelectDataSourceWindowChanged;
    //        _windowWatcher = null;

    //        _syncContextAuto.Post(delegate
    //        {
    //            Debug.Print("Disposing SelectDataSourceWatcher - In Automation context");
    //        }, null);
    //    }
    //}
}
