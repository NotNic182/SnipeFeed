using System;
using SnipeFeed.PC.Models;
using SnipeFeed.PC.Utils;

internal static class Program
{
    private static int _failures;

    private static void Check(string name, bool condition)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
        if (!condition) _failures++;
    }

    private static int Main()
    {
        // TMP renders entities literally, so escaping must NOT produce them.
        Check("no &amp; entities", !Formatting.EscapeForTmp("Rock & Roll").Contains("&amp;"));
        Check("ampersand kept verbatim", Formatting.EscapeForTmp("Rock & Roll") == "Rock & Roll");
        // '<' must survive visibly but never open a TMP tag: a zero-width
        // space directly after every '<'.
        Check("tag neutralized", Formatting.EscapeForTmp("<b>bold</b>") == "<​b>bold<​/b>");
        Check("heart name kept", Formatting.EscapeForTmp("<3 song") == "<​3 song");
        Check("null tolerated", Formatting.EscapeForTmp(null) == "");

        // TitleLine must route its user strings through the escaper.
        var entry = new FeedEntry { SongName = "<3", SongAuthor = "A<B", Mapper = "M" };
        var title = Formatting.TitleLine(entry);
        Check("TitleLine escapes songName", title.StartsWith("<​3"));
        Check("TitleLine escapes author", title.Contains("A<​B"));

        // TimeAgo boundaries.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Check("just now", Formatting.TimeAgo(now - 30) == "just now");
        Check("minutes", Formatting.TimeAgo(now - 120) == "2m ago");
        Check("future clamps", Formatting.TimeAgo(now + 999) == "just now");
        Check("zero is empty", Formatting.TimeAgo(0) == "");

        Console.WriteLine(_failures == 0 ? "ALL PASS" : _failures + " FAILURES");
        return _failures == 0 ? 0 : 1;
    }
}
