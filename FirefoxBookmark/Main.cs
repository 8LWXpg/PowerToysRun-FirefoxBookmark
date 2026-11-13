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

	[DllImport("user32.dll")]
	private static extern bool AllowSetForegroundWindow(uint dwProcessId);

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();

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
			Log.Warn($"FirefoxBookmark: virtual desktop manager unavailable: {ex.Message}", typeof(Main));
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
			Log.Warn($"FirefoxBookmark: IsWindowOnCurrentVirtualDesktop(0x{window:X}) failed: {ex.Message}", typeof(Main));
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

	private static bool HasWindowClass(IntPtr window, string expectedClass)
	{
		if (window == IntPtr.Zero)
		{
			return false;
		}

		var className = new StringBuilder(256);
		_ = GetClassName(window, className, className.Capacity);
		return className.ToString().Equals(expectedClass, StringComparison.Ordinal);
	}

	private static bool ForceForeground(IntPtr window)
	{
		if (window == IntPtr.Zero)
		{
			return false;
		}

		if (IsIconic(window))
		{
			_ = ShowWindow(window, SW_RESTORE);
		}

		if (SetForegroundWindow(window))
		{
			return true;
		}

		uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
		uint targetThread = GetWindowThreadProcessId(window, out _);
		if (foregroundThread == 0 || targetThread == 0 || foregroundThread == targetThread)
		{
			return false;
		}

		bool attached = false;
		try
		{
			attached = AttachThreadInput(foregroundThread, targetThread, true);
			_ = BringWindowToTop(window);
			return SetForegroundWindow(window);
		}
		finally
		{
			if (attached)
			{
				_ = AttachThreadInput(foregroundThread, targetThread, false);
			}
		}
	}

	private bool OpenWithFocus(Func<bool> openExisting, Func<bool> openNewWindow, bool focusExistingAfter)
	{
		// Grant foreground rights while PowerToys Run is still the foreground process.
		_ = AllowSetForegroundWindow(ASFW_ANY);

		string? processName = ResolveProcessName(_browserName);
		IntPtr anchor = IntPtr.Zero;
		int windowCount = 0;

		if (processName is not null)
		{
			IVirtualDesktopManager? virtualDesktopManager = CreateVirtualDesktopManager();
			List<IntPtr> windows = GetFirefoxWindows(processName);
			windowCount = windows.Count;

			foreach (IntPtr window in windows)
			{
				if (IsOnCurrentDesktop(virtualDesktopManager, window))
				{
					anchor = window;
					break;
				}
			}

			if (anchor != IntPtr.Zero)
			{
				_ = GetWindowThreadProcessId(anchor, out uint anchorPid);
				bool focused = ForceForeground(anchor);
				Log.Info(
					$"FirefoxBookmark: current-desktop window 0x{anchor:X} pid={anchorPid} focused={focused} totalWindows={windowCount} title='{GetWindowTitle(anchor)}'",
					typeof(Main));

				// Give Firefox a moment to register the activated window as its most recent one.
				Thread.Sleep(50);

				bool opened = openExisting();
				if (opened && focusExistingAfter)
				{
					// Only force focus if Firefox did not already bring a browser window forward.
					if (!HasWindowClass(GetForegroundWindow(), FirefoxWindowClass))
					{
						_ = ForceForeground(anchor);
					}
				}

				return opened;
			}
		}

		bool useNewWindow = windowCount > 0;
		Log.Info(
			$"FirefoxBookmark: no current-desktop Firefox window, totalWindows={windowCount}, useNewWindow={useNewWindow}",
			typeof(Main));

		return useNewWindow ? openNewWindow() : openExisting();
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
