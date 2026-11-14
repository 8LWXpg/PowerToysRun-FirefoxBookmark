using Community.PowerToys.Run.Plugin.FirefoxBookmark.Properties;
using ManagedCommon;
using Microsoft.PowerToys.Settings.UI.Library;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wox.Infrastructure;
using Wox.Plugin;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.FirefoxBookmark;

public class Main : IPlugin, IPluginI18n, IContextMenu, ISettingProvider, IReloadable, IDisposable
{
	private const uint ASFW_ANY = unchecked((uint)-1);
	private const int SW_RESTORE = 9;
	private const uint GW_OWNER = 4;
	private const string FirefoxWindowClass = "MozillaWindowClass";
	private const uint INPUT_MOUSE = 0;
	private const uint WM_NULL = 0x0000;
	private const uint SMTO_ABORTIFHUNG = 0x0002;

	[DllImport("user32.dll")]
	private static extern bool AllowSetForegroundWindow(uint dwProcessId);

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();

	[DllImport("user32.dll", SetLastError = true)]
	private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

	[DllImport("user32.dll")]
	private static extern bool IsIconic(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

	[DllImport("user32.dll")]
	private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

	[DllImport("user32.dll")]
	private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

	[DllImport("user32.dll")]
	private static extern bool BringWindowToTop(IntPtr hWnd);

	private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

	[StructLayout(LayoutKind.Sequential)]
	private struct INPUT
	{
		public uint type;
		public MOUSEINPUT mi;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MOUSEINPUT
	{
		public int dx;
		public int dy;
		public uint mouseData;
		public uint dwFlags;
		public uint time;
		public IntPtr dwExtraInfo;
	}

	[ComImport]
	[Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A")]
	private class VirtualDesktopManagerCom
	{
	}

	[ComImport]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	[Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
	private interface IVirtualDesktopManager
	{
		[PreserveSig]
		int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);

		[PreserveSig]
		int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);

		[PreserveSig]
		int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
	}

	private const string BrowserPath = nameof(BrowserPath);
	private string? _browserPath;
	private const string BrowserName = nameof(BrowserName);
	private string? _browserName;
	private const string IgnoreBookmarklets = nameof(IgnoreBookmarklets);
	private bool _ignoreBookmarklets;
	private string? _favoriteIcon;

	private PluginInitContext? _context;
	private bool _disposed;
	public string Name => Resources.plugin_name;
	public string Description => Resources.plugin_desc;
	public static string PluginID => "845da8375a4c4b1fb625f3992481e15c";

	public IEnumerable<PluginAdditionalOption> AdditionalOptions =>
		[
			new ()
			{
				PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Textbox,
				Key = BrowserPath,
				DisplayLabel = Resources.settings_profile_path,
				DisplayDescription = Resources.settings_profile_desc,
				TextValue = "Mozilla\\Firefox",
			},
			new ()
			{
				PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Textbox,
				Key = BrowserName,
				DisplayLabel = Resources.settings_browser_name,
				DisplayDescription = Resources.settings_browser_name_desc,
				TextValue = "firefox",
			},
			new()
			{
				PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Checkbox,
				Key = IgnoreBookmarklets,
				DisplayLabel = Resources.settings_ignore_bookmarklets,
				DisplayDescription = Resources.settings_ignore_bookmarklets_desc,
			}
		];

	public void UpdateSettings(PowerLauncherPluginSettings settings)
	{
		_browserPath = settings?.AdditionalOptions?.FirstOrDefault(x => x.Key == BrowserPath)?.TextValue ?? "Mozilla\\Firefox";
		_browserName = settings?.AdditionalOptions?.FirstOrDefault(x => x.Key == BrowserName)?.TextValue ?? "firefox";
		_ignoreBookmarklets = settings?.AdditionalOptions?.FirstOrDefault(x => x.Key == IgnoreBookmarklets)?.Value ?? false;
	}

	public List<ContextMenuResult> LoadContextMenus(Result selectedResult)
	{
		var bookmark = (Bookmark)selectedResult.ContextData;
		return [
			new()
			{
				Title = Resources.context_copy_url,
				Glyph = "\xE8C8",
				FontFamily = "Segoe Fluent Icons,Segoe MDL2 Assets",
				AcceleratorKey = Key.C,
				AcceleratorModifiers = ModifierKeys.Control,
				Action = _ =>
				{
					Clipboard.SetText(bookmark.Url);
					return true;
				}
			},
			new ()
			{
				Title = Resources.context_open_new,
				Glyph = "\xE8A7",
				FontFamily = "Segoe Fluent Icons,Segoe MDL2 Assets",
				AcceleratorKey = Key.N,
				AcceleratorModifiers = ModifierKeys.Control,
				Action = _ => OpenWithFocus(
					() => bookmark.OpenNewWindow(_browserName!),
					() => bookmark.OpenNewWindow(_browserName!),
					focusExistingAfter: false),
			},
			new ()
			{
				Title = Resources.context_open_private,
				Glyph = "\xE727",
				FontFamily = "Segoe Fluent Icons,Segoe MDL2 Assets",
				AcceleratorKey = Key.P,
				AcceleratorModifiers = ModifierKeys.Control,
				Action = _ => OpenWithFocus(
					() => bookmark.OpenPrivate(_browserName!),
					() => bookmark.OpenPrivate(_browserName!),
					focusExistingAfter: false),
			}
		];
	}

	public List<Result> Query(Query query)
	{
		ArgumentNullException.ThrowIfNull(query);

		List<Bookmark> bookmarks = [];

		var profiles = $"{Environment.GetEnvironmentVariable("APPDATA")}\\{_browserPath}\\Profiles";
		foreach (var profile in Directory.GetDirectories(profiles))
		{
			var dbPath = $"{profile}\\places.sqlite";
			if (!File.Exists(dbPath))
			{
				continue;
			}

			bookmarks.AddRange(Bookmark.GetBookmarks(dbPath));
		}

		if (_ignoreBookmarklets)
		{
			_ = bookmarks.RemoveAll(b => b.Url.StartsWith("javascript:"));
		}

		List<Result> results = bookmarks.ConvertAll(b =>
		{
			MatchResult match = StringMatcher.FuzzySearch(query.Search, b.Title);
			return new Result
			{
				Title = b.Title,
				SubTitle = b.Url,
				ToolTipData = new ToolTipData(b.Title, b.Url),
				IcoPath = _favoriteIcon,
				Score = match.Score,
				TitleHighlightData = match.MatchData,
				ContextData = b,
				Action = _ => OpenWithFocus(
					() => b.Open(_browserName!),
					() => b.OpenNewWindow(_browserName!),
					focusExistingAfter: true),
			};
		});

		if (!string.IsNullOrEmpty(query.Search))
		{
			_ = results.RemoveAll(r => r.Score <= 0);
		}

		return results;
	}

	private static string? ResolveProcessName(string? browserName)
	{
		if (string.IsNullOrWhiteSpace(browserName))
		{
			return null;
		}

		var exe = Path.GetFileNameWithoutExtension(browserName.Trim().Trim('"'));
		return string.IsNullOrWhiteSpace(exe) ? null : exe;
	}

	private static IVirtualDesktopManager? CreateVirtualDesktopManager()
	{
		try
		{
			return (IVirtualDesktopManager)new VirtualDesktopManagerCom();
		}
		catch (Exception ex)
		{
			// Log.Warn($"FirefoxBookmark: virtual desktop manager unavailable: {ex.Message}", typeof(Main));
			return null;
		}
	}

	private static bool IsOnCurrentDesktop(IVirtualDesktopManager? virtualDesktopManager, IntPtr window)
	{
		if (virtualDesktopManager is null)
		{
			return true;
		}

		try
		{
			return virtualDesktopManager.IsWindowOnCurrentVirtualDesktop(window, out int onCurrentDesktop) == 0
				&& onCurrentDesktop != 0;
		}
		catch (Exception ex)
		{
			// Log.Warn($"FirefoxBookmark: IsWindowOnCurrentVirtualDesktop(0x{window:X}) failed: {ex.Message}", typeof(Main));
			return true;
		}
	}

	private static List<IntPtr> GetFirefoxWindows(string processName)
	{
		HashSet<uint> pids = [];
		foreach (Process process in Process.GetProcessesByName(processName))
		{
			using (process)
			{
				_ = pids.Add((uint)process.Id);
			}
		}

		List<IntPtr> windows = [];
		if (pids.Count == 0)
		{
			return windows;
		}

		_ = EnumWindows((window, lParam) =>
		{
			if (!IsWindowVisible(window) || GetWindow(window, GW_OWNER) != IntPtr.Zero)
			{
				return true;
			}

			_ = GetWindowThreadProcessId(window, out uint pid);
			if (!pids.Contains(pid))
			{
				return true;
			}

			if (!HasWindowClass(window, FirefoxWindowClass))
			{
				return true;
			}

			windows.Add(window);
			return true;
		}, IntPtr.Zero);

		return windows;
	}

	private static string GetWindowTitle(IntPtr window)
	{
		var title = new StringBuilder(512);
		_ = GetWindowText(window, title, title.Capacity);
		return title.ToString();
	}

	private static string GetWindowClass(IntPtr window)
	{
		if (window == IntPtr.Zero)
		{
			return "none";
		}

		var className = new StringBuilder(256);
		_ = GetClassName(window, className, className.Capacity);
		return className.ToString();
	}

	private static bool HasWindowClass(IntPtr window, string expectedClass)
	{
		return GetWindowClass(window).Equals(expectedClass, StringComparison.Ordinal);
	}

	private static string DescribeWindow(IntPtr window)
	{
		if (window == IntPtr.Zero)
		{
			return "0x0";
		}

		return $"0x{window:X} class='{GetWindowClass(window)}' title='{GetWindowTitle(window)}'";
	}

	private static bool BringToForeground(IntPtr window)
	{
		if (window == IntPtr.Zero)
		{
			return false;
		}

		if (GetForegroundWindow() == window)
		{
			return true;
		}

		if (IsIconic(window))
		{
			_ = ShowWindow(window, SW_RESTORE);
		}

		_ = SetForegroundWindow(window);

		if (GetForegroundWindow() != window)
		{
			// Make this process the last-input process so Windows lets us bypass the foreground lock.
			var input = new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT() };
			_ = SendInput(1, [input], Marshal.SizeOf<INPUT>());
			_ = SetForegroundWindow(window);
		}

		if (GetForegroundWindow() != window)
		{
			uint targetThread = GetWindowThreadProcessId(window, out _);
			uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
			if (targetThread != 0 && foregroundThread != 0 && targetThread != foregroundThread)
			{
				bool attached = false;
				try
				{
					attached = AttachThreadInput(targetThread, foregroundThread, true);
					_ = BringWindowToTop(window);
					_ = SetForegroundWindow(window);
				}
				finally
				{
					if (attached)
					{
						_ = AttachThreadInput(targetThread, foregroundThread, false);
					}
				}
			}
		}

		return GetForegroundWindow() == window;
	}

	private static void FlushActivation(IntPtr window)
	{
		if (window == IntPtr.Zero)
		{
			return;
		}

		_ = SendMessageTimeout(window, WM_NULL, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 500, out _);
	}

	private static void ScheduleFocus(IntPtr target, string source)
	{
		_ = Task.Run(async () =>
		{
			for (int attempt = 1; attempt <= 3; attempt++)
			{
				try
				{
					// Wait until PowerToys Run has hidden and the desktop focus has settled.
					await Task.Delay(attempt == 1 ? 300 : 400).ConfigureAwait(false);

					IntPtr foreground = GetForegroundWindow();
					if (HasWindowClass(foreground, FirefoxWindowClass))
					{
						// Log.Info($"FirefoxBookmark: {source}: Firefox already foreground after attempt {attempt}: {DescribeWindow(foreground)}", typeof(Main));
						return;
					}

					bool activated = BringToForeground(target);
					// Log.Info($"FirefoxBookmark: {source}: focus attempt {attempt} activated={activated} foreground={DescribeWindow(GetForegroundWindow())}", typeof(Main));
					if (activated)
					{
						FlushActivation(target);
						return;
					}
				}
				catch (Exception)
				{
					// Log.Exception($"FirefoxBookmark: {source}: focus attempt {attempt} failed", ex, typeof(Main));
				}
			}
		});
	}

	private static void ScheduleNewWindowFocus(string? processName, List<IntPtr> existingWindows, string source)
	{
		if (processName is null)
		{
			return;
		}

		var before = new HashSet<IntPtr>(existingWindows);
		_ = Task.Run(async () =>
		{
			try
			{
				for (int attempt = 1; attempt <= 20; attempt++)
				{
					await Task.Delay(100).ConfigureAwait(false);

					IntPtr newWindow = IntPtr.Zero;
					foreach (IntPtr window in GetFirefoxWindows(processName))
					{
						if (!before.Contains(window))
						{
							newWindow = window;
							break;
						}
					}

					if (newWindow == IntPtr.Zero)
					{
						continue;
					}

					IntPtr foreground = GetForegroundWindow();
					if (HasWindowClass(foreground, FirefoxWindowClass))
					{
						// Log.Info($"FirefoxBookmark: {source}: new window {DescribeWindow(newWindow)} created; Firefox already foreground: {DescribeWindow(foreground)}", typeof(Main));
						return;
					}

					bool activated = BringToForeground(newWindow);
					// Log.Info($"FirefoxBookmark: {source}: new window {DescribeWindow(newWindow)} activated={activated} foreground={DescribeWindow(GetForegroundWindow())}", typeof(Main));
					if (activated)
					{
						FlushActivation(newWindow);
					}

					return;
				}

				// Log.Warn($"FirefoxBookmark: {source}: no new Firefox window detected within 2000ms", typeof(Main));
			}
			catch (Exception)
			{
				// Log.Exception($"FirefoxBookmark: {source}: new window focus failed", ex, typeof(Main));
			}
		});
	}

	private bool OpenWithFocus(Func<bool> openExisting, Func<bool> openNewWindow, bool focusExistingAfter)
	{
		// Grant foreground rights while PowerToys Run is still the foreground process.
		_ = AllowSetForegroundWindow(ASFW_ANY);

		string? processName = ResolveProcessName(_browserName);
		List<IntPtr> windows = processName is null ? new List<IntPtr>() : GetFirefoxWindows(processName);
		int windowCount = windows.Count;
		IntPtr anchor = IntPtr.Zero;

		if (processName is not null && windowCount > 0)
		{
			IVirtualDesktopManager? virtualDesktopManager = CreateVirtualDesktopManager();
			foreach (IntPtr window in windows)
			{
				if (IsOnCurrentDesktop(virtualDesktopManager, window))
				{
					anchor = window;
					break;
				}
			}
		}

		if (anchor != IntPtr.Zero)
		{
			_ = BringToForeground(anchor);
			FlushActivation(anchor);
			// Log.Info(
			//     $"FirefoxBookmark: current-desktop window 0x{anchor:X} pid={anchorPid} activated={activated} foreground={DescribeWindow(GetForegroundWindow())} totalWindows={windowCount}",
			//     typeof(Main));

			// Give Firefox time to register the activated window as its most recent one before the URL arrives.
			Thread.Sleep(100);

			bool opened = focusExistingAfter ? openExisting() : openNewWindow();
			if (opened)
			{
				if (focusExistingAfter)
				{
					ScheduleFocus(anchor, "tab");
				}
				else
				{
					ScheduleNewWindowFocus(processName, windows, "new-window");
				}
			}

			return opened;
		}

		bool useNewWindow = windowCount > 0;
		// Log.Info(
		//     $"FirefoxBookmark: no current-desktop Firefox window, totalWindows={windowCount}, useNewWindow={useNewWindow}",
		//     typeof(Main));

		if (useNewWindow)
		{
			bool opened = openNewWindow();
			if (opened)
			{
				ScheduleNewWindowFocus(processName, windows, "fallback-new-window");
			}

			return opened;
		}

		bool coldStarted = openExisting();
		if (coldStarted)
		{
			ScheduleNewWindowFocus(processName, windows, "cold-start");
		}

		return coldStarted;
	}

	public void Init(PluginInitContext context)
	{
		_context = context ?? throw new ArgumentNullException(nameof(context));
		_context.API.ThemeChanged += OnThemeChanged;
		UpdateIconPath(_context.API.GetCurrentTheme());
	}

	public string GetTranslatedPluginTitle() => Resources.plugin_name;

	public string GetTranslatedPluginDescription() => Resources.plugin_desc;

	private void OnThemeChanged(Theme oldTheme, Theme newTheme) => UpdateIconPath(newTheme);

	private void UpdateIconPath(Theme theme)
	{
		_favoriteIcon = theme is Theme.Light or Theme.HighContrastWhite
			? @"Images\Favorite.light.png"
			: @"Images\Favorite.dark.png";
	}

	public Control CreateSettingPanel() => throw new NotImplementedException();

	public void ReloadData()
	{
		if (_context is null)
		{
			return;
		}

		UpdateIconPath(_context.API.GetCurrentTheme());
	}

	public void Dispose()
	{
		Dispose(true);
		GC.SuppressFinalize(this);
	}

	protected virtual void Dispose(bool disposing)
	{
		if (!_disposed && disposing)
		{
			if (_context != null && _context.API != null)
			{
				_context.API.ThemeChanged -= OnThemeChanged;
			}

			_disposed = true;
		}
	}
}
