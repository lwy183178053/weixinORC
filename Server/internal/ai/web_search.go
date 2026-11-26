package ai

import (
	"context"
	"encoding/xml"
	"fmt"
	"html"
	"io"
	"net/http"
	"net/url"
	"regexp"
	"strings"
	"time"
)

type WebSearchResult struct {
	Title   string `json:"title"`
	Snippet string `json:"snippet"`
	URL     string `json:"url,omitempty"`
}

var (
	webSearchClient = &http.Client{
		Timeout: 8 * time.Second,
		Transport: &http.Transport{
			Proxy:               http.ProxyFromEnvironment,
			MaxIdleConns:        16,
			MaxIdleConnsPerHost: 8,
			IdleConnTimeout:     60 * time.Second,
		},
	}

	// Overridable in unit tests for deterministic offline verification.
	bingSearchBaseURL = "https://cn.bing.com/search"
	ddgSearchBaseURL  = "https://html.duckduckgo.com/html/"

	htmlTagRe     = regexp.MustCompile(`(?s)<[^>]+>`)
	multiSpaceRe  = regexp.MustCompile(`\s+`)
	bingAlgoRe    = regexp.MustCompile(`(?is)<li[^>]*class="[^"]*b_algo[^"]*"[^>]*>(.*?)</li>`)
	bingTitleRe   = regexp.MustCompile(`(?is)<h2[^>]*>.*?<a[^>]*href="([^"]+)"[^>]*>(.*?)</a>`)
	bingSnippetRe = regexp.MustCompile(`(?is)<div[^>]*class="[^"]*b_caption[^"]*"[^>]*>.*?<p[^>]*>(.*?)</p>`)
	bingAltSnipRe = regexp.MustCompile(`(?is)<p[^>]*class="[^"]*b_lineclamp[^"]*"[^>]*>(.*?)</p>`)
	ddgResultRe   = regexp.MustCompile(`(?is)<a[^>]*class="[^"]*result__a[^"]*"[^>]*href="([^"]+)"[^>]*>(.*?)</a>`)
	ddgSnippetRe  = regexp.MustCompile(`(?is)<(?:a|div)[^>]*class="[^"]*result__snippet[^"]*"[^>]*>(.*?)</(?:a|div)>`)
)

// performWebSearch executes a free, zero-API-key web search:
// 1. cn.bing.com RSS XML (fast ~4KB payload, structured XML, reliable in China)
// 2. cn.bing.com HTML fallback
// 3. html.duckduckgo.com fallback
func performWebSearch(ctx context.Context, query string, maxResults int) ([]WebSearchResult, string, error) {
	q := strings.TrimSpace(query)
	if q == "" {
		return nil, "", fmt.Errorf("搜索关键词不能为空")
	}
	maxResults = clampInt(maxResults, 1, 6)

	rssResults, rssErr := searchBingRSS(ctx, q, maxResults)
	if rssErr == nil && len(rssResults) > 0 {
		return rssResults, "bing_rss", nil
	}

	htmlResults, htmlErr := searchBingCNHTML(ctx, q, maxResults)
	if htmlErr == nil && len(htmlResults) > 0 {
		return htmlResults, "bing_html", nil
	}

	ddgResults, ddgErr := searchDuckDuckGoHTML(ctx, q, maxResults)
	if ddgErr == nil && len(ddgResults) > 0 {
		return ddgResults, "duckduckgo", nil
	}

	if rssErr != nil {
		return nil, "bing_rss", rssErr
	}
	if htmlErr != nil {
		return nil, "bing_html", htmlErr
	}
	if ddgErr != nil {
		return nil, "duckduckgo", ddgErr
	}
	return nil, "bing_rss", nil
}

func fetchSearchBody(ctx context.Context, rawURL, accept string) (string, error) {
	reqCtx, cancel := context.WithTimeout(ctx, 8*time.Second)
	defer cancel()

	req, err := http.NewRequestWithContext(reqCtx, http.MethodGet, rawURL, nil)
	if err != nil {
		return "", err
	}
	req.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36")
	req.Header.Set("Accept", accept)
	req.Header.Set("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8")

	resp, err := webSearchClient.Do(req)
	if err != nil {
		return "", err
	}
	defer resp.Body.Close()

	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return "", fmt.Errorf("搜索服务返回 HTTP %d", resp.StatusCode)
	}
	raw, err := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if err != nil {
		return "", err
	}
	return string(raw), nil
}

type bingRSSFeed struct {
	Channel struct {
		Items []struct {
			Title       string `xml:"title"`
			Link        string `xml:"link"`
			Description string `xml:"description"`
		} `xml:"item"`
	} `xml:"channel"`
}

func searchBingRSS(ctx context.Context, query string, maxResults int) ([]WebSearchResult, error) {
	endpoint := fmt.Sprintf("%s?q=%s&format=rss&setlang=zh-Hans&ensearch=0", bingSearchBaseURL, url.QueryEscape(query))
	body, err := fetchSearchBody(ctx, endpoint, "application/rss+xml,application/xml,text/xml;q=0.9,*/*;q=0.8")
	if err != nil {
		return nil, err
	}
	var feed bingRSSFeed
	if err := xml.Unmarshal([]byte(body), &feed); err != nil {
		return nil, err
	}
	results := make([]WebSearchResult, 0, maxResults)
	seen := make(map[string]bool, maxResults)
	for _, it := range feed.Channel.Items {
		title := stripHTMLToText(it.Title)
		if title == "" || seen[title] {
			continue
		}
		seen[title] = true
		results = append(results, WebSearchResult{
			Title:   truncateRunes(title, 120),
			Snippet: truncateRunes(stripHTMLToText(it.Description), 300),
			URL:     strings.TrimSpace(it.Link),
		})
		if len(results) >= maxResults {
			break
		}
	}
	return results, nil
}

func searchBingCNHTML(ctx context.Context, query string, maxResults int) ([]WebSearchResult, error) {
	endpoint := fmt.Sprintf("%s?q=%s&setlang=zh-Hans&ensearch=0", bingSearchBaseURL, url.QueryEscape(query))
	page, err := fetchSearchBody(ctx, endpoint, "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8")
	if err != nil {
		return nil, err
	}

	blocks := bingAlgoRe.FindAllStringSubmatch(page, maxResults*2)
	results := make([]WebSearchResult, 0, maxResults)
	seen := make(map[string]bool, maxResults)
	for _, b := range blocks {
		if len(b) < 2 {
			continue
		}
		tm := bingTitleRe.FindStringSubmatch(b[1])
		if len(tm) < 3 {
			continue
		}
		link := strings.TrimSpace(html.UnescapeString(tm[1]))
		title := stripHTMLToText(tm[2])
		if title == "" || seen[title] {
			continue
		}

		snippet := ""
		if sm := bingSnippetRe.FindStringSubmatch(b[1]); len(sm) >= 2 {
			snippet = stripHTMLToText(sm[1])
		} else if sm := bingAltSnipRe.FindStringSubmatch(b[1]); len(sm) >= 2 {
			snippet = stripHTMLToText(sm[1])
		}

		seen[title] = true
		results = append(results, WebSearchResult{
			Title:   truncateRunes(title, 120),
			Snippet: truncateRunes(snippet, 280),
			URL:     link,
		})
		if len(results) >= maxResults {
			break
		}
	}
	return results, nil
}

func searchDuckDuckGoHTML(ctx context.Context, query string, maxResults int) ([]WebSearchResult, error) {
	endpoint := fmt.Sprintf("%s?q=%s&kl=cn-zh", ddgSearchBaseURL, url.QueryEscape(query))
	page, err := fetchSearchBody(ctx, endpoint, "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8")
	if err != nil {
		return nil, err
	}

	titles := ddgResultRe.FindAllStringSubmatch(page, maxResults*2)
	snippets := ddgSnippetRe.FindAllStringSubmatch(page, maxResults*2)
	results := make([]WebSearchResult, 0, maxResults)
	seen := make(map[string]bool, maxResults)
	for i, tm := range titles {
		if len(tm) < 3 {
			continue
		}
		link := normalizeDDGLink(strings.TrimSpace(html.UnescapeString(tm[1])))
		title := stripHTMLToText(tm[2])
		if title == "" || seen[title] {
			continue
		}
		snippet := ""
		if i < len(snippets) && len(snippets[i]) >= 2 {
			snippet = stripHTMLToText(snippets[i][1])
		}
		seen[title] = true
		results = append(results, WebSearchResult{
			Title:   truncateRunes(title, 120),
			Snippet: truncateRunes(snippet, 280),
			URL:     link,
		})
		if len(results) >= maxResults {
			break
		}
	}
	return results, nil
}

func normalizeDDGLink(raw string) string {
	if strings.Contains(raw, "uddg=") {
		if u, err := url.Parse(raw); err == nil {
			if target := u.Query().Get("uddg"); target != "" {
				return target
			}
		}
	}
	return raw
}

func stripHTMLToText(s string) string {
	noTags := htmlTagRe.ReplaceAllString(s, " ")
	unescaped := html.UnescapeString(noTags)
	return strings.TrimSpace(multiSpaceRe.ReplaceAllString(unescaped, " "))
}
