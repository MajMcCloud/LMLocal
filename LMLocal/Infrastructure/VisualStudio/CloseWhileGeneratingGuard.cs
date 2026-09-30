using System;
using System.Runtime.InteropServices;
using LMLocal.Application.Abstractions.Ports;
using LMLocal.Application.ChatSession;
using LMLocal.Core.Common;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace LMLocal.Infrastructure.VisualStudio
{
    /// <summary>
    /// Guards Visual Studio against being closed by accident while an AI request is running.
    /// </summary>
    internal interface ICloseWhileGeneratingGuard : IDisposable
    {
        /// <summary>
        /// Starts listening for close requests of the Visual Studio main window.
        /// Must be called on the UI thread.
        /// </summary>
        void Initialize();
    }

    /// <summary>
    /// Intercepts close requests of the Visual Studio main window while an AI request is running and
    /// asks the user how to proceed: continue working, cancel the work and stay, or exit immediately.
    /// </summary>
    /// <remarks>
    /// Two channels are monitored because they cover different ways of closing Visual Studio:
    /// <list type="bullet">
    /// <item><description><c>WM_CLOSE</c> on the main window handle (close button, Alt+F4).</description></item>
    /// <item><description><c>WM_QUERYENDSESSION</c> broadcast (logoff / shutdown / "close all windows").</description></item>
    /// </list>
    /// Both handlers answer synchronously: a running request keeps the UI thread busy, so asynchronous
    /// event sinks would not be serviced in time.
    /// </remarks>
    internal sealed class CloseWhileGeneratingGuard : ICloseWhileGeneratingGuard
    {
        private const int WM_CLOSE = 0x0010;
        private const int WM_QUERYENDSESSION = 0x0011;

        // Custom task dialog button ids.
        private const int ButtonContinueWork = 100;
        private const int ButtonCancelAndStay = 101;
        private const int ButtonCancelAndExit = 102;

        // Fallback message box constants / results.
        private const uint MB_YESNOCANCEL = 0x00000003;
        private const uint MB_ICONWARNING = 0x00000030;
        private const int IDYES = 6;
        private const int IDNO = 7;
        private const int IDCANCEL = 2;

        private const string DialogTitle = "LM Local";
        private const string MainInstruction = "An AI request is still running.";
        private const string ContentText = "Closing Visual Studio now would cancel the current work.\r\nWhat would you like to do?";

        private readonly ISessionManager _sessionManager;
        private readonly ISettingsManager _settingsManager;

        private IVsShell _shell;
        private BroadcastMessageEvents _broadcastSink;
        private uint _broadcastCookie;

        private IntPtr _mainWindowHandle = IntPtr.Zero;
        private WndProcDelegate _wndProc;
        private IntPtr _originalWndProc = IntPtr.Zero;
        private bool _subclassingApplied;

        // Prevents a second prompt while one is open, and remembers an explicit "exit now".
        private bool _isHandling;
        private bool _exitApproved;
        private bool _disposed;

        public CloseWhileGeneratingGuard(ISessionManager sessionManager, ISettingsManager settingsManager)
        {
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
        }

        public void Initialize()
        {
            if (_disposed) return;

            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                _shell = Package.GetGlobalService(typeof(SVsShell)) as IVsShell;
                var uiShell = Package.GetGlobalService(typeof(SVsUIShell)) as IVsUIShell;

                IntPtr hwnd = IntPtr.Zero;
                if (uiShell != null && ErrorHandler.Succeeded(uiShell.GetDialogOwnerHwnd(out hwnd)))
                {
                    _mainWindowHandle = hwnd;
                }

                if (_mainWindowHandle == IntPtr.Zero)
                {
                    InternalLogger.Warn("CloseWhileGeneratingGuard: Main window handle not available, the guard stays inactive.");
                    return;
                }

                if (_shell != null)
                {
                    _broadcastSink = new BroadcastMessageEvents(this);
                    int hr = _shell.AdviseBroadcastMessages(_broadcastSink, out _broadcastCookie);
                    if (!ErrorHandler.Succeeded(hr))
                    {
                        InternalLogger.Warn($"CloseWhileGeneratingGuard: AdviseBroadcastMessages failed (0x{hr:X8}); session-end interception is disabled.");
                        _broadcastCookie = 0;
                    }
                }

                SubclassMainWindow();
            }
            catch (Exception ex)
            {
                InternalLogger.Error("CloseWhileGeneratingGuard: Failed to initialize the close guard.", ex);
            }
        }

        /// <summary>
        /// Handles an incoming close request.
        /// Returns true when the caller must ignore (veto) the close attempt.
        /// </summary>
        private bool TryVetoClose(string source)
        {
            if (_disposed || _exitApproved)
                return false;

            // A prompt is already open: keep Visual Studio open and do not stack dialogs.
            if (_isHandling)
                return true;

            if (!IsPreventionEnabled() || !IsWorkRunning())
                return false;

            _isHandling = true;
            try
            {
                CloseChoice choice = PromptUser();
                InternalLogger.Info($"CloseWhileGeneratingGuard: '{source}' intercepted, user chose '{choice}'.");

                switch (choice)
                {
                    case CloseChoice.CancelAndExit:
                        // Let this and any following close request through.
                        _exitApproved = true;
                        return false;

                    case CloseChoice.CancelWorkAndStay:
                        CancelRunningWork();
                        return true;

                    default:
                        // Continue work (also used for Esc / X) -> keep Visual Studio open.
                        return true;
                }
            }
            catch (Exception ex)
            {
                InternalLogger.Error("CloseWhileGeneratingGuard: Failed to handle the close request.", ex);
                return false;
            }
            finally
            {
                _isHandling = false;
            }
        }

        private bool IsPreventionEnabled()
        {
            try
            {
                return _settingsManager?.Current?.PreventCloseWhileGenerating ?? true;
            }
            catch (InvalidOperationException)
            {
                // Settings not loaded yet: keep the safe default.
                return true;
            }
            catch (Exception ex)
            {
                InternalLogger.Warn($"CloseWhileGeneratingGuard: Could not read settings: {ex.Message}");
                return false;
            }
        }

        private bool IsWorkRunning()
        {
            try
            {
                return _sessionManager?.IsSessionRunning ?? false;
            }
            catch (Exception ex)
            {
                InternalLogger.Warn($"CloseWhileGeneratingGuard: Could not read session state: {ex.Message}");
                return false;
            }
        }

        private void CancelRunningWork()
        {
            try
            {
                _sessionManager.TryStopSession();
            }
            catch (Exception ex)
            {
                InternalLogger.Error("CloseWhileGeneratingGuard: Failed to cancel the running session.", ex);
            }
        }

        private CloseChoice PromptUser()
        {
            // Both entry points (window procedure and broadcast sink) run on the UI thread, so the modal
            // dialog can be shown directly. A nested modal loop is safe here: it pumps messages while the
            // AI request continues in the background.
            ThreadHelper.ThrowIfNotOnUIThread();
            return ShowClosePrompt();
        }

        private CloseChoice ShowClosePrompt()
        {
            if (TryShowTaskDialog(_mainWindowHandle, out CloseChoice choice))
                return choice;

            return ShowFallbackMessageBox(_mainWindowHandle);
        }

        private static bool TryShowTaskDialog(IntPtr owner, out CloseChoice choice)
        {
            choice = CloseChoice.ContinueWork;

            IntPtr buttonsPtr = IntPtr.Zero;
            int hr;
            int button;
            int radioButton;
            bool verificationChecked;

            try
            {
                TASKDIALOG_BUTTON[] buttons =
                {
                    new TASKDIALOG_BUTTON(ButtonContinueWork, "Continue work"),
                    new TASKDIALOG_BUTTON(ButtonCancelAndStay, "Cancel work in progress & stay"),
                    new TASKDIALOG_BUTTON(ButtonCancelAndExit, "Cancel & exit now")
                };

                int buttonSize = Marshal.SizeOf(typeof(TASKDIALOG_BUTTON));
                buttonsPtr = Marshal.AllocHGlobal(buttonSize * buttons.Length);

                for (int i = 0; i < buttons.Length; i++)
                {
                    Marshal.StructureToPtr(buttons[i], IntPtr.Add(buttonsPtr, buttonSize * i), false);
                }

                TASKDIALOGCONFIG config = new TASKDIALOGCONFIG();
                config.cbSize = (uint)Marshal.SizeOf(typeof(TASKDIALOGCONFIG));
                config.hwndParent = owner;
                config.hInstance = IntPtr.Zero;
                config.dwFlags = TDF_ALLOW_DIALOG_CANCELLATION | TDF_USE_COMMAND_LINKS;
                config.dwCommonButtons = 0;
                config.pszWindowTitle = DialogTitle;
                config.hMainIcon = TD_WARNING_ICON;
                config.pszMainInstruction = MainInstruction;
                config.pszContent = ContentText;
                config.cButtons = (uint)buttons.Length;
                config.pButtons = buttonsPtr;
                config.nDefaultButton = ButtonContinueWork;
                config.cRadioButtons = 0;
                config.pRadioButtons = IntPtr.Zero;
                config.nDefaultRadioButton = 0;
                config.pszVerificationFlagText = null;
                config.dwVerificationFlagState = 0;
                config.nDefaultVerificationFlag = 0;

                hr = TaskDialogIndirect(ref config, out button, out radioButton, out verificationChecked);
            }
            finally
            {
                if (buttonsPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buttonsPtr);
                }
            }

            if (hr != 0)
                return false;

            switch (button)
            {
                case ButtonCancelAndExit:
                    choice = CloseChoice.CancelAndExit;
                    return true;
                case ButtonCancelAndStay:
                    choice = CloseChoice.CancelWorkAndStay;
                    return true;
                default:
                    // ButtonContinueWork, IDCANCEL (Esc / X) or anything unexpected.
                    choice = CloseChoice.ContinueWork;
                    return true;
            }
        }

        private static CloseChoice ShowFallbackMessageBox(IntPtr owner)
        {
            string text = MainInstruction + "\r\n\r\n" +
                          "[Yes]    Continue work (keep Visual Studio open)\r\n" +
                          "[No]     Cancel & exit now\r\n" +
                          "[Cancel] Cancel work in progress & stay";

            int result = MessageBoxW(owner, text, DialogTitle, MB_YESNOCANCEL | MB_ICONWARNING);

            switch (result)
            {
                case IDNO:
                    return CloseChoice.CancelAndExit;
                case IDCANCEL:
                    return CloseChoice.CancelWorkAndStay;
                default:
                    // IDYES or unexpected result -> stay safe and keep working.
                    return CloseChoice.ContinueWork;
            }
        }

        #region Main window subclassing (WM_CLOSE)

        private void SubclassMainWindow()
        {
            if (_mainWindowHandle == IntPtr.Zero || _subclassingApplied)
                return;

            try
            {
                _wndProc = new WndProcDelegate(MainWindowProc);
                IntPtr newProc = Marshal.GetFunctionPointerForDelegate(_wndProc);

                _originalWndProc = Is64BitProcess
                    ? SetWindowLongPtr(_mainWindowHandle, GWLP_WNDPROC, newProc)
                    : SetWindowLong32(_mainWindowHandle, GWLP_WNDPROC, newProc);

                if (_originalWndProc == IntPtr.Zero)
                {
                    InternalLogger.Warn("CloseWhileGeneratingGuard: SetWindowLong failed, WM_CLOSE interception is disabled.");
                    _wndProc = null;
                    return;
                }

                // Keep the delegate alive for as long as the window procedure is subclassed.
                GC.KeepAlive(_wndProc);
                _subclassingApplied = true;
            }
            catch (Exception ex)
            {
                InternalLogger.Error("CloseWhileGeneratingGuard: Failed to subclass the Visual Studio main window.", ex);
                _wndProc = null;
                _subclassingApplied = false;
            }
        }

        private void RestoreMainWindow()
        {
            if (!_subclassingApplied || _mainWindowHandle == IntPtr.Zero || _originalWndProc == IntPtr.Zero)
                return;

            try
            {
                if (Is64BitProcess)
                    SetWindowLongPtr(_mainWindowHandle, GWLP_WNDPROC, _originalWndProc);
                else
                    SetWindowLong32(_mainWindowHandle, GWLP_WNDPROC, _originalWndProc);
            }
            catch (Exception ex)
            {
                InternalLogger.Warn($"CloseWhileGeneratingGuard: Failed to restore the main window procedure: {ex.Message}");
            }
            finally
            {
                _subclassingApplied = false;
                _originalWndProc = IntPtr.Zero;
                _wndProc = null;
            }
        }

        private IntPtr MainWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            if (_originalWndProc == IntPtr.Zero)
                return CallWindowProcFallback(hWnd, msg, wParam, lParam);

            if (msg == WM_CLOSE && TryVetoClose("WM_CLOSE"))
                return IntPtr.Zero;

            return CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
        }

        private static IntPtr CallWindowProcFallback(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                return DefWindowProc(hWnd, msg, wParam, lParam);
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        #endregion

        #region Broadcast messages (WM_QUERYENDSESSION)

        private int OnBroadcastMessage(int message, IntPtr wParam, IntPtr lParam)
        {
            if (message == WM_QUERYENDSESSION && TryVetoClose("WM_QUERYENDSESSION"))
            {
                // Signal the shell that the session end should not proceed.
                return VS_E_FALSE;
            }

            return VS_S_OK;
        }

        private sealed class BroadcastMessageEvents : IVsBroadcastMessageEvents
        {
            private readonly CloseWhileGeneratingGuard _owner;

            internal BroadcastMessageEvents(CloseWhileGeneratingGuard owner)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            }

            public int OnBroadcastMessage(uint msg, IntPtr wParam, IntPtr lParam)
            {
                return _owner.OnBroadcastMessage((int)msg, wParam, lParam);
            }
        }

        #endregion

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_shell != null && _broadcastCookie != 0)
                {
                    _shell.UnadviseBroadcastMessages(_broadcastCookie);
                }
            }
            catch (Exception ex)
            {
                InternalLogger.Warn($"CloseWhileGeneratingGuard: Failed to unadvise broadcast messages: {ex.Message}");
            }
            finally
            {
                _broadcastCookie = 0;
                _broadcastSink = null;
                _shell = null;
            }

            RestoreMainWindow();
        }

        private enum CloseChoice
        {
            ContinueWork,
            CancelWorkAndStay,
            CancelAndExit
        }

        #region Win32 interop

        private const int GWLP_WNDPROC = -4;
        private const int VS_S_OK = 0;
        private const int VS_E_FALSE = 1;

        private static readonly bool Is64BitProcess = IntPtr.Size == 8;

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        #endregion

        #region TaskDialog interop

        private const uint TDF_ALLOW_DIALOG_CANCELLATION = 0x0008;
        private const uint TDF_USE_COMMAND_LINKS = 0x0010;

        // TD_WARNING_ICON == MAKEINTRESOURCEW(-2).
        private static readonly IntPtr TD_WARNING_ICON = new IntPtr(-2);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TASKDIALOG_BUTTON
        {
            public int nButtonID;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pszButtonText;

            public TASKDIALOG_BUTTON(int id, string text)
            {
                nButtonID = id;
                pszButtonText = text;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TASKDIALOGCONFIG
        {
            public uint cbSize;
            public IntPtr hwndParent;
            public IntPtr hInstance;
            public uint dwFlags;
            public uint dwCommonButtons;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pszWindowTitle;
            public IntPtr hMainIcon;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pszMainInstruction;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pszContent;
            public uint cButtons;
            public IntPtr pButtons;
            public int nDefaultButton;
            public uint cRadioButtons;
            public IntPtr pRadioButtons;
            public int nDefaultRadioButton;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pszVerificationFlagText;
            public uint dwVerificationFlagState;
            public uint nDefaultVerificationFlag;
        }

        [DllImport("comctl32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int TaskDialogIndirect(
            ref TASKDIALOGCONFIG pTaskConfig,
            out int pnButton,
            out int pnRadioButton,
            [MarshalAs(UnmanagedType.Bool)] out bool pfVerificationFlagChecked);

        #endregion
    }
}
