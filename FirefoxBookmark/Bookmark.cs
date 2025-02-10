using Microsoft.Data.Sqlite;
using Wox.Infrastructure;

namespace Community.PowerToys.Run.Plugin.FirefoxBookmark;

public class Bookmark(string title, string url)
{
	public string Title { get; set; } = title;
	public string Url { get; set; } = url;

	public bool Open(string browserName) => Helper.OpenInShell(browserName, Url);
	public bool OpenNewWindow(string browserName) => Helper.OpenInShell(browserName, $"-new-window {Url}");
	public bool OpenPrivate(string browserName) => Helper.OpenInShell(browserName, $"-private-window {Url}");

	public static List<Bookmark> GetBookmarks(string dbPath)
	{
		List<Bookmark> bookmarks = [];

		var query = @"
            SELECT
                b.title,
                p.url
            FROM moz_bookmarks b
            INNER JOIN moz_places p ON b.fk = p.id";

		using (var connection = new SqliteConnection($"Data Source={dbPath}"))
		{
			connection.Open();

			using var command = new SqliteCommand(query, connection);
			using var reader = command.ExecuteReader();
			while (reader.Read())
			{
				bookmarks.Add(new Bookmark(
					reader.GetString(0),
					reader.GetString(1))
				);
			}
		}

		return bookmarks;
	}
}