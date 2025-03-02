using Community.PowerToys.Run.Plugin.FirefoxBookmark.Properties;
using ManagedCommon;
using Microsoft.PowerToys.Settings.UI.Library;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wox.Infrastructure;
using Wox.Plugin;

namespace Community.PowerToys.Run.Plugin.FirefoxBookmark;

public class Main : IPlugin, IPluginI18n, IContextMenu, ISettingProvider, IReloadable, IDisposable
{
	private const string browserPath = nameof(browserPath);
	private const string browserName = nameof(browserName);
	private string? _browserPath;
	private string? _browserName;
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
				Key = browserPath,
				DisplayLabel = Resources.settings_profile_path,
				DisplayDescription = Resources.settings_profile_desc,
				TextValue = "Mozilla\\Firefox",
			},
			new ()
			{
				PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Textbox,
				Key = browserName,
				DisplayLabel = Resources.settings_browser_name,
				DisplayDescription = Resources.settings_browser_name_desc,
				TextValue = "firefox",
			},
		];

	public void UpdateSettings(PowerLauncherPluginSettings settings)
	{
		_browserPath = settings?.AdditionalOptions?.FirstOrDefault(x => x.Key == browserPath)?.TextValue ?? "Mozilla\\Firefox";
		_browserName = settings?.AdditionalOptions?.FirstOrDefault(x => x.Key == browserName)?.TextValue ?? "firefox";
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
				Action = _ => bookmark.OpenNewWindow(_browserName!),
			},
			new ()
			{
				Title = Resources.context_open_private,
				Glyph = "\xE727",
				FontFamily = "Segoe Fluent Icons,Segoe MDL2 Assets",
				AcceleratorKey = Key.P,
				AcceleratorModifiers = ModifierKeys.Control,
				Action = _ => bookmark.OpenPrivate(_browserName!),
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
				Action = _ => b.Open(_browserName!),
			};
		});

		if (!string.IsNullOrEmpty(query.Search))
		{
			_ = results.RemoveAll(r => r.Score <= 0);
		}

		return results;
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
