using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

var branches = new[] { "cfc", "district5" };
var keyword = Environment.GetEnvironmentVariable("MOVIE_KEYWORD") ?? "doomsday";
// Hall names aren't fixed ("Standard", "Standard & Deluxe", ...), so alert on anything except these.
var excludedExperiences = (Environment.GetEnvironmentVariable("EXCLUDED_EXPERIENCES") ?? "ScreenX,Screen X,4DX")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
// Pages known ahead of time, probed even if they drop off the homepage listing.
var knownSlugs = new[] { "avengers-doomsday-infinity-vision-sx-4dx" };
var ntfyTopic = Environment.GetEnvironmentVariable("NTFY_TOPIC");
var statePath = Environment.GetEnvironmentVariable("STATE_FILE") ?? "state.json";

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.UserAgent.ParseAdd(
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
var parser = new HtmlParser();

if (args.Contains("--test"))
{
    await Notify("Scene watcher test", "If you see this, notifications work.", "https://cfc.scenecinemas.com", "default");
    return 0;
}

var found = new List<Showing>();
var failures = 0;

foreach (var branch in branches)
{
    var baseUrl = $"https://{branch}.scenecinemas.com";
    var movieUrls = new HashSet<string>(knownSlugs.Select(s => $"{baseUrl}/movie-details/{s}.html"));

    foreach (var listing in new[] { baseUrl + "/", baseUrl + "/next_releasing" })
    {
        var html = await Get(listing);
        if (html is null) { failures++; continue; }
        foreach (var a in parser.ParseDocument(html).QuerySelectorAll("a[href*='/movie-details/']"))
        {
            var href = a.GetAttribute("href")!;
            var label = $"{href} {a.GetAttribute("title")} {a.TextContent}";
            if (label.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                movieUrls.Add(href.Split('?')[0]);
        }
    }

    foreach (var movieUrl in movieUrls)
    {
        var page = await Get(movieUrl);
        if (page is null) continue;
        var days = Regex.Matches(page, @"LoadShowtimes\('(\d{2}-\d{2}-\d{4})'\)")
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        Console.WriteLine($"[{branch}] {movieUrl}: {days.Count} day(s)");

        foreach (var day in days)
        {
            var fragment = await Get($"{movieUrl}?business_day={day}&ajax=1");
            if (fragment is null) { failures++; continue; }
            foreach (var block in parser.ParseDocument(fragment).QuerySelectorAll(".ex_stand_content"))
            {
                var experience = block.QuerySelector(".ex_stand")?.TextContent.Trim() ?? "";
                var times = block.QuerySelectorAll("li a").Select(t => t.TextContent.Trim()).Where(t => t.Length > 0).ToList();
                Console.WriteLine($"  {day} {experience}: {string.Join(", ", times)}");
                if (times.Count > 0 && !excludedExperiences.Any(x => experience.Contains(x, StringComparison.OrdinalIgnoreCase)))
                    found.Add(new Showing(branch, experience, day, times, movieUrl));
            }
        }
    }
}

var notified = File.Exists(statePath)
    ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(statePath)) ?? []
    : [];
var fresh = found.Where(s => !notified.Contains(s.Key)).ToList();
Console.WriteLine($"Matches: {found.Count}, new: {fresh.Count}, failed requests: {failures}");

if (fresh.Count > 0)
{
    var body = string.Join("\n", fresh.Select(s => $"{s.Branch.ToUpper()} · {s.Experience} · {s.Day}: {string.Join(", ", s.Times)}"));
    await Notify("Avengers Doomsday booking OPEN", body, fresh[0].Url, "urgent");
    notified.UnionWith(fresh.Select(s => s.Key));
    File.WriteAllText(statePath, JsonSerializer.Serialize(notified.Order(), new JsonSerializerOptions { WriteIndented = true }));
}

return 0;

async Task<string?> Get(string url)
{
    try
    {
        using var response = await http.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"  ! {url} -> {(int)response.StatusCode}");
            return null;
        }
        return await response.Content.ReadAsStringAsync();
    }
    catch (Exception e)
    {
        Console.WriteLine($"  ! {url} -> {e.Message}");
        return null;
    }
}

async Task Notify(string title, string body, string clickUrl, string priority)
{
    if (string.IsNullOrWhiteSpace(ntfyTopic))
    {
        Console.WriteLine($"NTFY_TOPIC not set; would notify:\n{title}\n{body}");
        return;
    }
    using var request = new HttpRequestMessage(HttpMethod.Post, $"https://ntfy.sh/{ntfyTopic}")
    {
        Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("text/plain")),
    };
    request.Headers.Add("Title", title);
    request.Headers.Add("Priority", priority);
    request.Headers.Add("Tags", "rotating_light,movie_camera");
    request.Headers.Add("Click", clickUrl);
    using var response = await http.SendAsync(request);
    Console.WriteLine($"ntfy -> {(int)response.StatusCode}");
    response.EnsureSuccessStatusCode();
}

record Showing(string Branch, string Experience, string Day, List<string> Times, string Url)
{
    public string Key => $"{Branch}|{Experience}|{Day}";
}
