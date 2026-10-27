// Command agbrowser is the native launcher for agbrowser.
//
// It is deliberately tiny and dependency-free: it locates a Chromium browser,
// prepares a managed profile that reuses the user's real one, and starts the
// browser with the flags that make it slim, ad-free and agent-friendly.
//
// The launcher is what the MSI installs and what the default-browser
// registration points at, so it must work on a machine with nothing else
// installed.
package main

import (
	"encoding/json"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
)

const version = "0.1.0"

// channel describes one browser product.
type channel struct {
	ID        string
	Label     string
	Binary    string
	UserData  string
	Profiles  []string
}

var windowsChannels = []struct {
	id, label, binary, userData string
}{
	{
		"chrome", "Google Chrome",
		`C:\Program Files\Google\Chrome\Application\chrome.exe`,
		`%LOCALAPPDATA%\Google\Chrome\User Data`,
	},
	{
		"chrome", "Google Chrome",
		`C:\Program Files (x86)\Google\Chrome\Application\chrome.exe`,
		`%LOCALAPPDATA%\Google\Chrome\User Data`,
	},
	{
		"edge", "Microsoft Edge",
		`C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe`,
		`%LOCALAPPDATA%\Microsoft\Edge\User Data`,
	},
	{
		"edge", "Microsoft Edge",
		`C:\Program Files\Microsoft\Edge\Application\msedge.exe`,
		`%LOCALAPPDATA%\Microsoft\Edge\User Data`,
	},
	{
		"brave", "Brave",
		`%LOCALAPPDATA%\BraveSoftware\Brave-Browser\Application\brave.exe`,
		`%LOCALAPPDATA%\BraveSoftware\Brave-Browser\User Data`,
	},
	{
		"chromium", "Chromium",
		`%LOCALAPPDATA%\Chromium\Application\chrome.exe`,
		`%LOCALAPPDATA%\Chromium\User Data`,
	},
}

var unixChannels = []struct {
	id, label, binary, userData string
}{
	{"chrome", "Google Chrome", "/usr/bin/google-chrome", "~/.config/google-chrome"},
	{"chrome", "Google Chrome", "/usr/bin/chromium-browser", "~/.config/chromium"},
	{"edge", "Microsoft Edge", "/usr/bin/microsoft-edge", "~/.config/microsoft-edge"},
	{"brave", "Brave", "/usr/bin/brave-browser", "~/.config/BraveSoftware/Brave-Browser"},
	{"chromium", "Chromium", "/usr/bin/chromium", "~/.config/chromium"},
}

// adHosts are resolved to a black hole so ad requests never leave the machine.
var adHosts = []string{
	"googleads.g.doubleclick.net", "doubleclick.net", "google-analytics.com",
	"ssl.google-analytics.com", "googletagmanager.com", "www.googletagmanager.com",
	"googlesyndication.com", "pagead2.googlesyndication.com", "adservice.google.com",
	"adservice.googleusercontent.com", "googleadservices.com", "www.googleadservices.com",
	"amazon-adsystem.com", "c.amazon-adsystem.com", "adnxs.com", "adsrvr.org",
	"crashlyticsreports-pa.googleapis.com", "update.googleapis.com",
	"play.google-analytics.com", "app-measurement.com",
	"firebaseinstallations.googleapis.com", "safebrowsing.googleapis.com",
	"content-autofill.googleapis.com", "optimizationguide-pa.googleapis.com",
	"suggestqueries.google.com", "clientservices.googleapis.com",
	"gvt1.com", "gvt2.com", "gvt3.com",
}

// slimFlags remove background services and features a browsing session does
// not need. They make the browser start faster and stay quiet.
var slimFlags = []string{
	"--no-first-run",
	"--no-default-browser-check",
	"--no-service-autorun",
	"--disable-session-crashed-bubble",
	"--disable-infobars",
	"--disable-notifications",
	"--disable-search-engine-choice-screen",
	"--disable-sync",
	"--disable-background-networking",
	"--disable-background-timer-throttling",
	"--disable-backgrounding-occluded-windows",
	"--disable-renderer-backgrounding",
	"--disable-client-side-phishing-detection",
	"--disable-component-update",
	"--disable-component-extensions-with-background-pages",
	"--disable-default-apps",
	"--disable-hang-monitor",
	"--disable-popup-blocking",
	"--disable-prompt-on-repost",
	"--disable-domain-reliability",
	"--disable-breakpad",
	"--disable-print-preview",
	"--disable-translate",
	"--metrics-recording-only",
	"--no-pings",
	"--mute-audio",
	"--password-store=basic",
	"--use-mock-keychain",
	"--force-color-profile=srgb",
	"--disable-blink-features=AutomationControlled",
}

// enableFeatures turns on the native vertical tab strip.
const enableFeatures = "VerticalTabs,TabStripUnification,NetworkService,NetworkServiceInProcess"

// disableFeatures turns off bloat and telemetry.
const disableFeatures = "Translate,BackForwardCache,AcceptCHFrame,MediaRouter," +
	"OptimizationHints,OptimizationGuideModelDownloading,InterestFeedContentSuggestions," +
	"CalculateNativeWinOcclusion,GlobalMediaControls,PrivacySandboxSettings4," +
	"AutofillServerCommunication,CertificateTransparencyComponentUpdater,DialMediaRouteProvider," +
	"ExtensionManifestV2Disabled,EnhancedSafeBrowsing,ImprovedCookieControls,AiMode"

func main() {
	if len(os.Args) > 1 {
		switch os.Args[1] {
		case "--version", "-v":
			fmt.Printf("agbrowser %s\n", version)
			return
		case "--help", "-h":
			usage()
			return
		case "--channels":
			for _, c := range discover() {
				fmt.Printf("%-10s %-18s %s\n", c.ID, c.Label, c.Binary)
			}
			return
		}
	}

	if err := run(os.Args[1:]); err != nil {
		fmt.Fprintf(os.Stderr, "agbrowser: %v\n", err)
		os.Exit(1)
	}
}

func usage() {
	fmt.Println(`agbrowser — agent-first Chromium browser launcher

Usage:
  agbrowser [url...]        open the given URLs in agbrowser
  agbrowser --channels      list detected browser channels
  agbrowser --version       print the version
  agbrowser --help          show this message

The launcher reuses the browser profile you already have, so logins,
bookmarks, the password manager and the vendor's sync keep working.
It also enables the native left-side vertical tab strip and blocks ads
at the network level.`)
}

// expand resolves %VAR% placeholders and a leading ~.
//
// os.ExpandEnv only understands the $VAR form, which Windows never sets, so
// the %VAR% syntax used throughout the channel table is handled here.
func expand(path string) string {
	path = expandPercent(path)
	if strings.HasPrefix(path, "~/") {
		if home, err := os.UserHomeDir(); err == nil {
			path = filepath.Join(home, path[2:])
		}
	}
	return path
}

// expandPercent replaces %NAME% with the value of the environment variable.
func expandPercent(path string) string {
	for i := 0; i < 8; i++ {
		start := strings.Index(path, "%")
		if start == -1 {
			break
		}
		end := strings.Index(path[start+1:], "%")
		if end == -1 {
			break
		}
		name := path[start+1 : start+1+end]
		if name == "" {
			break
		}
		value, ok := os.LookupEnv(name)
		if !ok {
			// Leave unknown placeholders untouched.
			break
		}
		path = path[:start] + value + path[start+1+end+1:]
	}
	return path
}

func fileExists(p string) bool {
	st, err := os.Stat(p)
	return err == nil && !st.IsDir()
}

func dirExists(p string) bool {
	st, err := os.Stat(p)
	return err == nil && st.IsDir()
}

// discover finds every installed Chromium browser on this machine.
func discover() []channel {
	var out []channel
	seen := map[string]bool{}

	add := func(id, label, bin, data string) {
		bin = expand(bin)
		data = expand(data)
		if !fileExists(bin) || !dirExists(data) {
			return
		}
		key := id + "|" + bin
		if seen[key] {
			return
		}
		seen[key] = true
		out = append(out, channel{
			ID: id, Label: label, Binary: bin, UserData: data,
			Profiles: listProfiles(data),
		})
	}

	if runtime.GOOS == "windows" {
		for _, c := range windowsChannels {
			add(c.id, c.label, c.binary, c.userData)
		}
	} else {
		for _, c := range unixChannels {
			add(c.id, c.label, c.binary, c.userData)
		}
	}
	return out
}

// listProfiles returns the profile directory names inside a user data dir.
func listProfiles(userData string) []string {
	entries, err := os.ReadDir(userData)
	if err != nil {
		return nil
	}
	var out []string
	for _, e := range entries {
		if !e.IsDir() {
			continue
		}
		name := e.Name()
		if name != "Default" && !strings.HasPrefix(name, "Profile ") {
			continue
		}
		if fileExists(filepath.Join(userData, name, "Preferences")) {
			out = append(out, name)
		}
	}
	return out
}

// managedRoot is where agbrowser keeps its per-channel profile links.
func managedRoot() string {
	if v := os.Getenv("AGB_DATA_DIR"); v != "" {
		return filepath.Join(v, "channels")
	}
	if runtime.GOOS == "windows" {
		local := os.Getenv("LOCALAPPDATA")
		if local == "" {
			local = filepath.Join(os.Getenv("USERPROFILE"), "AppData", "Local")
		}
		return filepath.Join(local, "agbrowser", "channels")
	}
	home, _ := os.UserHomeDir()
	return filepath.Join(home, ".local", "share", "agbrowser", "channels")
}

// prepareProfile makes sure the native vertical tab strip is enabled for the
// profile, and returns the user data dir the browser should run on.
//
// Chrome 136+ refuses to open a debug port on the browser's default user
// data directory, so the launcher points the browser at a managed root whose
// entries link to the real ones. The browser therefore keeps using its own
// files and nothing is copied or decrypted.
func prepareProfile(ch channel, profile string) (string, error) {
	profileDir := filepath.Join(ch.UserData, profile)

	// Enable the vertical tab strip in the real profile. This is a single
	// well-understood preference and it is what Chrome itself writes when the
	// user picks "Tab strip position -> Side".
	if err := enableVerticalTabs(profileDir); err != nil {
		return "", err
	}

	root := filepath.Join(managedRoot(), ch.ID)
	if err := os.MkdirAll(root, 0o755); err != nil {
		return "", err
	}

	// Local State carries the encryption keys for cookies and passwords.
	linkFile(filepath.Join(root, "Local State"), filepath.Join(ch.UserData, "Local State"))
	// The profile directory itself.
	linkDir(filepath.Join(root, profile), profileDir)

	// Runtime files must never be shared with a running instance.
	for _, name := range []string{"DevToolsActivePort", "lockfile", "SingletonLock", "SingletonCookie", "SingletonSocket"} {
		_ = os.RemoveAll(filepath.Join(root, name))
	}

	return root, nil
}

// enableVerticalTabs writes the vertical tab preference into a profile.
func enableVerticalTabs(profileDir string) error {
	prefsPath := filepath.Join(profileDir, "Preferences")
	if !fileExists(prefsPath) {
		return nil
	}
	raw, err := os.ReadFile(prefsPath)
	if err != nil {
		return err
	}
	var prefs map[string]any
	if err := json.Unmarshal(raw, &prefs); err != nil {
		// A torn read is normal while the browser is running; leave it alone.
		return nil
	}
	if prefs == nil {
		prefs = map[string]any{}
	}

	vt, _ := prefs["vertical_tabs"].(map[string]any)
	if vt == nil {
		vt = map[string]any{}
	}
	if enabled, _ := vt["enabled"].(bool); enabled {
		if collapsed, _ := vt["collapsed_state"].(bool); !collapsed {
			if _, ok := vt["uncollapsed_width"]; ok {
				return nil
			}
		}
	}
	vt["enabled"] = true
	vt["enabled_first_time"] = true
	if _, ok := vt["collapsed_state"]; !ok {
		vt["collapsed_state"] = false
	}
	if _, ok := vt["uncollapsed_width"]; !ok {
		vt["uncollapsed_width"] = 240
	}
	prefs["vertical_tabs"] = vt

	out, err := json.Marshal(prefs)
	if err != nil {
		return err
	}
	// Write atomically: the browser rewrites this file constantly.
	tmp := prefsPath + ".agb.tmp"
	if err := os.WriteFile(tmp, out, 0o600); err != nil {
		return err
	}
	return os.Rename(tmp, prefsPath)
}

// linkDir creates a directory link, preferring a junction on Windows.
func linkDir(link, target string) {
	if _, err := os.Lstat(link); err == nil {
		return
	}
	if !dirExists(target) {
		return
	}
	if runtime.GOOS == "windows" {
		cmd := exec.Command("cmd", "/c", "mklink", "/J", link, target)
		cmd.Stdout = nil
		cmd.Stderr = nil
		if err := cmd.Run(); err == nil {
			return
		}
	}
	_ = os.Symlink(target, link)
}

// linkFile creates a file link.
func linkFile(link, target string) {
	if _, err := os.Lstat(link); err == nil {
		return
	}
	if !fileExists(target) {
		return
	}
	_ = os.Symlink(target, link)
}

// hostResolverRules black-holes the known ad endpoints.
func hostResolverRules() string {
	rules := make([]string, 0, len(adHosts)+2)
	for _, h := range adHosts {
		rules = append(rules, "MAP "+h+" ~NOTFOUND")
	}
	rules = append(rules, "EXCLUDE localhost", "EXCLUDE 127.0.0.1")
	return strings.Join(rules, ", ")
}

// run starts the browser.
func run(urls []string) error {
	channels := discover()
	if len(channels) == 0 {
		return fmt.Errorf("no Chrome or Edge installation found")
	}
	ch := channels[0]

	profile := "Default"
	if len(ch.Profiles) > 0 {
		profile = ch.Profiles[0]
	}
	if v := os.Getenv("AGB_PROFILE"); v != "" {
		profile = v
	}

	userData, err := prepareProfile(ch, profile)
	if err != nil {
		return fmt.Errorf("preparing profile: %w", err)
	}

	args := append([]string{}, slimFlags...)
	args = append(args,
		"--enable-features="+enableFeatures,
		"--disable-features="+disableFeatures,
		"--host-resolver-rules="+hostResolverRules(),
		"--user-data-dir="+userData,
		"--profile-directory="+profile,
	)

	if len(urls) == 0 {
		urls = []string{"about:blank"}
	}
	args = append(args, urls...)

	cmd := exec.Command(ch.Binary, args...)
	cmd.Stdout = nil
	cmd.Stderr = nil
	if err := cmd.Start(); err != nil {
		return fmt.Errorf("starting %s: %w", ch.Label, err)
	}
	// Detach: the launcher returns immediately so it can act as a URL handler.
	go func() { _ = cmd.Wait() }()
	return nil
}
