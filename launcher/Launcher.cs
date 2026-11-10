// TalesUntold launcher: updates the client from manifest.json published to GitHub Releases (publish_github.py) and starts the game.
// Based on the L2Pure launcher. Manifest entries may carry an absolute "url" (a release asset); without it the file is taken from files/ next to the manifest.
// C# 5 / .NET Framework 4 so that it builds with the csc.exe shipped with every Windows (build.bat).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TalesUntoldLauncher
{
	public class ManifestFile
	{
		public string path;
		public long size;
		public string sha256;
		/// <summary>Optional download address (http/https, absolute or relative to the manifest).</summary>
		public string url;
	}

	public class Manifest
	{
		public string version;
		public List<ManifestFile> files;
	}

	public class CacheEntry
	{
		public long size;
		public long ticks;
		public string sha256;
	}

	/// <summary>Update logic without UI, so it can be tested.</summary>
	public class Updater
	{
		public readonly string Root;
		public readonly string ManifestUrl;
		public Action<string> Status = delegate { };
		public Action<long, long> Progress = delegate { };
		private string _selfUpdateFolder;
		private ManifestFile _selfUpdateFile;
		public bool StartPendingSelfUpdate(bool restart)
		{
			if (_selfUpdateFolder == null) return false;
			SelfUpdate.Start(_selfUpdateFolder, _selfUpdateFile, restart);
			return true;
		}
		private readonly string _cachePath;
		private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

		public Updater(string root, string manifestUrl)
		{
			Root = Path.GetFullPath(root);
			ManifestUrl = manifestUrl;
			_cachePath = Path.Combine(Root, "launcher-cache.json");
		}

		public static string FilesUrl(string manifestUrl)
		{
			return manifestUrl.Substring(0, manifestUrl.LastIndexOf('/') + 1) + "files/";
		}

		/// <summary>Where a manifest entry is downloaded from: its own url (GitHub release asset) or files/ next to the manifest.</summary>
		public string FileUrl(ManifestFile file)
		{
			if (string.IsNullOrEmpty(file.url))
			{
				return FilesUrl(ManifestUrl) + Uri.EscapeUriString(file.path);
			}
			Uri resolved = new Uri(new Uri(ManifestUrl), file.url);
			if ((resolved.Scheme != Uri.UriSchemeHttps) && (resolved.Scheme != Uri.UriSchemeHttp))
			{
				throw new InvalidDataException("Bad url in manifest: " + file.url);
			}
			return resolved.AbsoluteUri;
		}

		/// <summary>GitHub only accepts TLS 1.2+, which .NET 4 does not enable by default.</summary>
		public static void EnableModernTls()
		{
			try
			{
				ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072 | (SecurityProtocolType)12288;
			}
			catch (NotSupportedException)
			{
				ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
			}
		}

		/// <summary>Maps a manifest path to a local file and refuses anything outside the client folder.</summary>
		public string LocalPath(string relative)
		{
			if (string.IsNullOrEmpty(relative) || relative.Contains("..") || relative.Contains(":") || relative.StartsWith("/") || relative.StartsWith("\\"))
			{
				throw new InvalidDataException("Bad path in manifest: " + relative);
			}
			string full = Path.GetFullPath(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));
			if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidDataException("Path outside the client: " + relative);
			}
			return full;
		}

		public static string Sha256(string file)
		{
			using (SHA256 sha = SHA256.Create())
			using (FileStream stream = File.OpenRead(file))
			{
				byte[] hash = sha.ComputeHash(stream);
				StringBuilder sb = new StringBuilder(64);
				foreach (byte b in hash)
				{
					sb.Append(b.ToString("x2"));
				}
				return sb.ToString();
			}
		}

		public Manifest ParseManifest(string text)
		{
			Manifest manifest = _json.Deserialize<Manifest>(text);
			if ((manifest == null) || (manifest.files == null))
			{
				throw new InvalidDataException("Empty manifest");
			}
			return manifest;
		}

		private Dictionary<string, CacheEntry> LoadCache()
		{
			try
			{
				if (File.Exists(_cachePath))
				{
					return _json.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(_cachePath)) ?? new Dictionary<string, CacheEntry>();
				}
			}
			catch (Exception)
			{
				// A broken cache only means a full rehash.
			}
			return new Dictionary<string, CacheEntry>();
		}

		/// <summary>Files that are missing or differ from the manifest. With full=false unchanged files (size and time) reuse the cached hash.</summary>
		public List<ManifestFile> Compare(Manifest manifest, bool full)
		{
			Dictionary<string, CacheEntry> cache = full ? new Dictionary<string, CacheEntry>() : LoadCache();
			Dictionary<string, CacheEntry> fresh = new Dictionary<string, CacheEntry>();
			List<ManifestFile> outdated = new List<ManifestFile>();
			long total = 0, done = 0;
			foreach (ManifestFile file in manifest.files)
			{
				total += file.size;
			}
			foreach (ManifestFile file in manifest.files)
			{
				string local = LocalPath(file.path);
				Status("Проверка: " + file.path);
				FileInfo info = new FileInfo(local);
				if (!info.Exists || (info.Length != file.size))
				{
					outdated.Add(file);
				}
				else
				{
					CacheEntry entry;
					string hash;
					if (cache.TryGetValue(file.path, out entry) && (entry.size == info.Length) && (entry.ticks == info.LastWriteTimeUtc.Ticks))
					{
						hash = entry.sha256;
					}
					else
					{
						hash = Sha256(local);
					}
					fresh[file.path] = new CacheEntry { size = info.Length, ticks = info.LastWriteTimeUtc.Ticks, sha256 = hash };
					if (!string.Equals(hash, file.sha256, StringComparison.OrdinalIgnoreCase))
					{
						outdated.Add(file);
					}
				}
				done += file.size;
				Progress(done, total);
			}
			SaveCache(fresh);
			return outdated;
		}

		private void SaveCache(Dictionary<string, CacheEntry> cache)
		{
			try
			{
				File.WriteAllText(_cachePath, _json.Serialize(cache));
			}
			catch (Exception)
			{
				// Not critical.
			}
		}

		/// <summary>Downloads to a temporary file, checks size and SHA256, then replaces the old file. Three attempts per file.</summary>
		public void Download(List<ManifestFile> files)
		{
			long total = 0, done = 0;
			foreach (ManifestFile file in files)
			{
				total += file.size;
			}
			foreach (ManifestFile file in files)
			{
				string local = LocalPath(file.path);
				string temporary = local + ".download";
				Directory.CreateDirectory(Path.GetDirectoryName(local));
				Exception last = null;
				for (int attempt = 1; attempt <= 3; attempt++)
				{
					try
					{
						Status("Загрузка (" + attempt + "/3): " + file.path);
						using (WebClient client = new WebClient())
						{
							client.DownloadFile(FileUrl(file), temporary);
						}
						if ((new FileInfo(temporary).Length != file.size) || !string.Equals(Sha256(temporary), file.sha256, StringComparison.OrdinalIgnoreCase))
						{
							throw new InvalidDataException("Checksum mismatch: " + file.path);
						}
						if (SelfUpdate.IsSelf(local))
						{
							if (_selfUpdateFolder != null) throw new InvalidDataException("Duplicate self update entry");
							_selfUpdateFolder = SelfUpdate.Stage(Root, temporary, file);
							_selfUpdateFile = file;
							last = null;
							break;
						}
						if (File.Exists(local))
						{
							File.SetAttributes(local, FileAttributes.Normal);
							File.Delete(local);
						}
						File.Move(temporary, local);
						last = null;
						break;
					}
					catch (Exception e)
					{
						last = e;
						if (File.Exists(temporary))
						{
							File.Delete(temporary);
						}
					}
				}
				if (last != null)
				{
					throw new IOException("Не удалось загрузить " + file.path + ": " + last.Message, last);
				}
				done += file.size;
				Progress(done, total);
			}
		}

		public string FetchManifest()
		{
			using (WebClient client = new WebClient())
			{
				client.Encoding = Encoding.UTF8;
				return client.DownloadString(ManifestUrl + "?t=" + DateTime.UtcNow.Ticks);
			}
		}
	}


	public sealed class SelfUpdateJob
	{
		public int parentPid;
		public long parentStarted;
		public long size;
		public string sha256;
		public bool restart;
	}

	public static class SelfUpdate
	{
		public const int PendingExitCode = 42;
		public const string FileName = "TalesUntoldLauncher.exe";
		public static string Executable { get { return Process.GetCurrentProcess().MainModule.FileName; } }
		public static bool IsSelf(string path)
		{
			return string.Equals(Path.GetFullPath(path), Path.GetFullPath(Executable), StringComparison.OrdinalIgnoreCase);
		}
		public static string Stage(string root, string downloaded, ManifestFile file)
		{
			if (!string.Equals(Path.GetFileName(Executable), FileName, StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException("Self update requires " + FileName);
			string folder = Path.Combine(root, ".launcher-update", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(folder);
			File.Move(downloaded, Path.Combine(folder, "staged.exe"));
			return folder;
		}
		public static void Start(string folder, ManifestFile file, bool restart)
		{
			string root = Directory.GetParent(Directory.GetParent(folder).FullName).FullName;
			if (!IsSelf(Path.Combine(root, FileName))) throw new InvalidDataException("Invalid self update root");
			Verify(Path.Combine(folder, "staged.exe"), file.size, file.sha256);
			SelfUpdateJob job = new SelfUpdateJob { parentPid = Process.GetCurrentProcess().Id,
				parentStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
				size = file.size, sha256 = file.sha256, restart = restart };
			File.WriteAllText(Path.Combine(folder, "job.json"), new JavaScriptSerializer().Serialize(job));
			string helper = Path.Combine(folder, "helper.exe");
			File.Copy(Executable, helper, false);
			Process.Start(new ProcessStartInfo(helper, "--self-update " + Path.GetFileName(folder)) {
				WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
				RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true }).Dispose();
		}
		private static void Verify(string path, long size, string hash)
		{
			if (size <= 0 || hash == null || !System.Text.RegularExpressions.Regex.IsMatch(hash, "\\A[0-9a-fA-F]{64}\\z") ||
				new FileInfo(path).Length != size || !string.Equals(Updater.Sha256(path), hash, StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException("Self update size/SHA256 mismatch");
		}
		public static int RunHelper(string[] args)
		{
			string folder = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
			string result = null;
			try
			{
				if (args.Length != 2 || !System.Text.RegularExpressions.Regex.IsMatch(args[1], "\\A[0-9a-f]{32}\\z") ||
					Path.GetFileName(folder) != args[1] || Directory.GetParent(folder).Name != ".launcher-update" ||
					!string.Equals(Path.GetFileName(Executable), "helper.exe", StringComparison.OrdinalIgnoreCase))
					throw new InvalidDataException("Invalid helper token/path");
				string root = Directory.GetParent(Directory.GetParent(folder).FullName).FullName;
				result = Path.Combine(folder, "result.json");
				SelfUpdateJob job = new JavaScriptSerializer().Deserialize<SelfUpdateJob>(File.ReadAllText(Path.Combine(folder, "job.json")));
				if (job == null || job.parentPid <= 0 || job.parentPid == Process.GetCurrentProcess().Id || job.parentStarted <= 0)
					throw new InvalidDataException("Invalid parent identity");
				string target = Path.Combine(root, FileName), staged = Path.Combine(folder, "staged.exe"), backup = Path.Combine(folder, "original.exe");
				Verify(staged, job.size, job.sha256);
				Process parent = null;
				try { parent = Process.GetProcessById(job.parentPid); } catch (ArgumentException) { }
				if (parent != null)
				{
					using (parent)
					{
						if (parent.StartTime.ToUniversalTime().Ticks == job.parentStarted)
						{
							if (!string.Equals(parent.MainModule.FileName, target, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Parent executable mismatch");
							if (!parent.WaitForExit(120000)) throw new TimeoutException("Parent did not exit within 120 seconds");
						}
					}
				}
				Verify(staged, job.size, job.sha256);
				Exception last = null;
				for (int attempt = 0; attempt < 40; attempt++)
				{
					try { File.Replace(staged, target, backup); last = null; break; }
					catch (IOException e) { last = e; Thread.Sleep(250); }
					catch (UnauthorizedAccessException e) { last = e; Thread.Sleep(250); }
				}
				if (last != null) throw new IOException("Self update replacement failed; original retained: " + last.Message, last);
				try
				{
					Verify(target, job.size, job.sha256);
					if (job.restart) Process.Start(new ProcessStartInfo(target) { WorkingDirectory = root, UseShellExecute = false });
				}
				catch
				{
					File.Replace(backup, target, Path.Combine(folder, "failed.exe"));
					throw;
				}
				File.WriteAllText(result, "{\"ok\":true}");
				return 0;
			}
			catch (Exception e)
			{
				if (result != null) File.WriteAllText(result, new JavaScriptSerializer().Serialize(new { ok = false, error = e.Message }));
				return 1;
			}
		}
	}

	/// <summary>One news or patch note entry from news.json.</summary>
	public class NewsItem
	{
		public string title;
		public string date;
		public string text;
	}

	public class NewsFeed
	{
		public List<NewsItem> news;
		public List<NewsItem> patchnotes;
	}

	/// <summary>Owner-drawn flat button with an accent style.</summary>
	public class FlatButton : Control
	{
		public bool Accent;
		public bool Selected;
		private bool _hover;
		private bool _down;

		public FlatButton()
		{
			SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
			BackColor = Color.Transparent;
			Cursor = Cursors.Hand;
			ForeColor = Color.FromArgb(235, 225, 210);
		}

		protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
		protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
		protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
		protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
		protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
		protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics g = e.Graphics;
			g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
			g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
			Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
			Color border = Theme.Border;
			if (Accent)
			{
				Color top = Enabled ? (_hover ? Theme.AccentLight : Theme.Accent) : Color.FromArgb(90, 80, 70);
				Color bottom = Enabled ? (_down ? Theme.AccentDark : Color.FromArgb(top.R * 3 / 4, top.G * 3 / 4, top.B * 3 / 4)) : Color.FromArgb(60, 55, 50);
				using (System.Drawing.Drawing2D.LinearGradientBrush brush = new System.Drawing.Drawing2D.LinearGradientBrush(r, top, bottom, 90f))
				{
					g.FillRectangle(brush, r);
				}
				using (Pen pen = new Pen(Color.FromArgb(120, 255, 230, 190)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot })
				{
					g.DrawRectangle(pen, 4, 4, Width - 9, Height - 9);
				}
				border = Theme.AccentDark;
			}
			else
			{
				using (SolidBrush brush = new SolidBrush(Selected ? Color.FromArgb(200, 120, 60, 25) : (_hover ? Color.FromArgb(150, 50, 40, 32) : Color.FromArgb(120, 20, 18, 16))))
				{
					g.FillRectangle(brush, r);
				}
				if (Selected)
				{
					border = Theme.Accent;
				}
			}
			using (Pen pen = new Pen(border))
			{
				g.DrawRectangle(pen, r);
			}
			Color text = Enabled ? (Accent ? Color.FromArgb(40, 20, 8) : (Selected || _hover ? Theme.AccentLight : ForeColor)) : Color.FromArgb(150, 140, 130);
			TextRenderer.DrawText(g, Text, Font, r, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
		}
	}

	public static class Theme
	{
		public static readonly Color Accent = Color.FromArgb(232, 131, 58);
		public static readonly Color AccentLight = Color.FromArgb(250, 170, 95);
		public static readonly Color AccentDark = Color.FromArgb(160, 80, 30);
		public static readonly Color Border = Color.FromArgb(90, 75, 60);
		public static readonly Color Text = Color.FromArgb(235, 225, 210);
		public static readonly Color Muted = Color.FromArgb(150, 140, 125);
		public static readonly Color Panel = Color.FromArgb(185, 22, 20, 18);
	}

	public class LauncherForm : Form
	{
		private readonly Updater _updater;
		private readonly string _gameExe;
		private readonly string _title;
		private readonly string _newsUrl;
		private readonly List<string[]> _servers = new List<string[]>();
		private readonly List<string[]> _links = new List<string[]>();
		private readonly Dictionary<string, bool> _online = new Dictionary<string, bool>();
		private readonly FlatButton _newsTab = new FlatButton();
		private readonly FlatButton _notesTab = new FlatButton();
		private readonly FlatButton _main = new FlatButton();
		private readonly Label _checkFiles = new Label();
		private Image _background;
		private NewsFeed _feed;
		private bool _showNotes;
		private string _status = "";
		private string _version = "";
		private long _done;
		private long _total;
		private int _outdated = -1;
		private volatile bool _busy;
		private Point _dragStart;

		public LauncherForm(Dictionary<string, string> config, string root)
		{
			string manifestUrl = Get(config, "ManifestUrl", "https://github.com/Sant0shiq/TalesUntold-updates/releases/latest/download/manifest.json");
			_updater = new Updater(root, manifestUrl);
			_gameExe = Path.Combine(root, Get(config, "GameExe", "system\\l2.exe"));
			_title = Get(config, "Title", "TALES UNTOLD");
			_newsUrl = Get(config, "NewsUrl", manifestUrl.Substring(0, manifestUrl.LastIndexOf('/') + 1) + "news.json");
			foreach (string server in Get(config, "Servers", "Tales Untold|93.170.20.7|7777").Split(';'))
			{
				string[] parts = server.Split('|');
				if (parts.Length == 3)
				{
					_servers.Add(parts);
				}
			}
			foreach (string link in Get(config, "Links", "").Split(';'))
			{
				string[] parts = link.Split('|');
				if ((parts.Length == 2) && (parts[1].Length > 0))
				{
					_links.Add(parts);
				}
			}
			string backgroundFile = Path.Combine(root, Get(config, "Background", "launcher-bg.jpg"));
			try
			{
				if (File.Exists(backgroundFile))
				{
					// Pre-scale once ("cover"), so painting is a plain bitmap copy.
					using (Image source = Image.FromFile(backgroundFile))
					{
						float scale = Math.Max(1100f / source.Width, 620f / source.Height);
						int w = (int) (source.Width * scale), h = (int) (source.Height * scale);
						Bitmap scaled = new Bitmap(1100, 620);
						using (Graphics g = Graphics.FromImage(scaled))
						{
							g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
							g.DrawImage(source, (1100 - w) / 2, (620 - h) / 2, w, h);
						}
						_background = scaled;
					}
				}
			}
			catch (Exception)
			{
				_background = null;
			}
			Text = _title;
			FormBorderStyle = FormBorderStyle.None;
			StartPosition = FormStartPosition.CenterScreen;
			ClientSize = new Size(1100, 620);
			DoubleBuffered = true;
			BackColor = Color.FromArgb(14, 12, 11);
			Font = new Font("Segoe UI", 9.5f);
			SetStyle(ControlStyles.ResizeRedraw, true);
			
			FlatButton close = TopButton("✕", 1056);
			close.Click += delegate { Close(); };
			FlatButton minimize = TopButton("—", 1016);
			minimize.Click += delegate { WindowState = FormWindowState.Minimized; };
			
			_newsTab.Text = "НОВОСТИ";
			_notesTab.Text = "ПАТЧНОУТ";
			_newsTab.Font = _notesTab.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
			_newsTab.SetBounds(40, 96, 130, 32);
			_notesTab.SetBounds(178, 96, 130, 32);
			_newsTab.Selected = true;
			_newsTab.Click += delegate { _showNotes = false; _newsTab.Selected = true; _notesTab.Selected = false; _newsTab.Invalidate(); _notesTab.Invalidate(); Invalidate(); };
			_notesTab.Click += delegate { _showNotes = true; _newsTab.Selected = false; _notesTab.Selected = true; _newsTab.Invalidate(); _notesTab.Invalidate(); Invalidate(); };
			Controls.Add(_newsTab);
			Controls.Add(_notesTab);
			
			int x = 40;
			foreach (string[] link in _links)
			{
				FlatButton button = new FlatButton { Text = link[0].ToUpperInvariant(), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };
				button.SetBounds(x, 470, 100, 36);
				string url = link[1];
				button.Click += delegate { OpenUrl(url); };
				Controls.Add(button);
				x += 108;
			}
			
			_main.Accent = true;
			_main.Text = "ОБНОВИТЬ";
			_main.Font = new Font("Segoe UI", 15f, FontStyle.Bold);
			_main.SetBounds(800, 530, 260, 60);
			_main.Click += delegate
			{
				if (_outdated == 0)
				{
					Play();
				}
				else
				{
					Run(false);
				}
			};
			Controls.Add(_main);
			
			_checkFiles.Text = "ПРОВЕРИТЬ ФАЙЛЫ";
			_checkFiles.ForeColor = Theme.Accent;
			_checkFiles.BackColor = Color.Transparent;
			_checkFiles.Font = new Font("Segoe UI", 8.5f, FontStyle.Underline);
			_checkFiles.AutoSize = true;
			_checkFiles.Cursor = Cursors.Hand;
			_checkFiles.Location = new Point(40 + TextRenderer.MeasureText(Shorten(root, 60).ToUpperInvariant(), new Font("Segoe UI", 8.5f)).Width + 20, 586);
			_checkFiles.Click += delegate { Run(true); };
			Controls.Add(_checkFiles);
			_rootText = Shorten(root, 60);
			
			MouseDown += (s, e) => _dragStart = e.Location;
			MouseMove += (s, e) =>
			{
				if ((e.Button == MouseButtons.Left) && (e.Y < 70))
				{
					Location = new Point(Location.X + e.X - _dragStart.X, Location.Y + e.Y - _dragStart.Y);
				}
			};
			
			_updater.Status = s => Ui(() => { _status = s; Invalidate(new Rectangle(0, 500, 790, 120)); });
			_updater.Progress = (done, total) => Ui(() => { _done = done; _total = total; Invalidate(new Rectangle(0, 500, 790, 120)); });
			Shown += delegate
			{
				CheckServers();
				LoadNews();
				Run(false);
			};
		}

		private readonly string _rootText;

		private FlatButton TopButton(string text, int x)
		{
			FlatButton button = new FlatButton { Text = text, Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
			button.SetBounds(x, 14, 34, 30);
			Controls.Add(button);
			return button;
		}

		private static string Shorten(string text, int max)
		{
			return text.Length <= max ? text : "..." + text.Substring(text.Length - max + 3);
		}

		private static string Get(Dictionary<string, string> config, string key, string fallback)
		{
			string value;
			return config.TryGetValue(key, out value) && (value.Length > 0) ? value : fallback;
		}

		private static void OpenUrl(string url)
		{
			if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
			{
				try
				{
					Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
				}
				catch (Exception)
				{
					// No browser available.
				}
			}
		}

		private void Ui(Action action)
		{
			if (IsDisposed || !IsHandleCreated)
			{
				return;
			}
			if (InvokeRequired)
			{
				BeginInvoke(action);
			}
			else
			{
				action();
			}
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics g = e.Graphics;
			g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
			g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
			Rectangle all = ClientRectangle;
			if (_background != null)
			{
				g.DrawImageUnscaled(_background, 0, 0);
			}
			else
			{
				using (System.Drawing.Drawing2D.LinearGradientBrush brush = new System.Drawing.Drawing2D.LinearGradientBrush(all, Color.FromArgb(38, 30, 26), Color.FromArgb(10, 9, 9), 60f))
				{
					g.FillRectangle(brush, all);
				}
				using (System.Drawing.Drawing2D.GraphicsPath glow = new System.Drawing.Drawing2D.GraphicsPath())
				{
					glow.AddEllipse(560, -120, 700, 640);
					using (System.Drawing.Drawing2D.PathGradientBrush brush = new System.Drawing.Drawing2D.PathGradientBrush(glow) { CenterColor = Color.FromArgb(70, 232, 131, 58), SurroundColors = new[] { Color.FromArgb(0, 0, 0, 0) } })
					{
						g.FillPath(brush, glow);
					}
				}
			}
			// Darken top and bottom bars.
			using (System.Drawing.Drawing2D.LinearGradientBrush top = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, Width, 70), Color.FromArgb(220, 8, 7, 6), Color.FromArgb(40, 8, 7, 6), 90f))
			{
				g.FillRectangle(top, 0, 0, Width, 70);
			}
			using (System.Drawing.Drawing2D.LinearGradientBrush bottom = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 500, Width, 120), Color.FromArgb(0, 8, 7, 6), Color.FromArgb(235, 8, 7, 6), 90f))
			{
				g.FillRectangle(bottom, 0, 500, Width, 120);
			}
			using (Pen pen = new Pen(Theme.Border))
			{
				g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
			}
			
			// Logo.
			using (Font logo = new Font("Segoe UI", 20f, FontStyle.Bold))
			using (SolidBrush accent = new SolidBrush(Theme.Accent))
			using (SolidBrush text = new SolidBrush(Theme.Text))
			{
				g.FillPolygon(accent, new[] { new Point(40, 22), new Point(54, 16), new Point(54, 40), new Point(40, 46) });
				g.DrawString(_title.ToUpperInvariant(), logo, text, 62, 14);
			}
			
			// Server status cards.
			int x = 600;
			using (Font name = new Font("Segoe UI", 9f, FontStyle.Bold))
			{
				foreach (string[] server in _servers)
				{
					Rectangle card = new Rectangle(x, 16, 190, 30);
					using (SolidBrush panel = new SolidBrush(Color.FromArgb(150, 20, 18, 16)))
					{
						g.FillRectangle(panel, card);
					}
					using (Pen pen = new Pen(Theme.Border))
					{
						g.DrawRectangle(pen, card);
					}
					bool known, online;
					known = _online.TryGetValue(server[0], out online);
					using (SolidBrush dot = new SolidBrush(!known ? Theme.Muted : (online ? Color.FromArgb(90, 210, 110) : Color.FromArgb(220, 70, 60))))
					{
						g.FillEllipse(dot, x + 12, 27, 9, 9);
					}
					TextRenderer.DrawText(g, server[0].ToUpperInvariant() + "  " + (!known ? "..." : (online ? "ОНЛАЙН" : "ОФФЛАЙН")), name, new Rectangle(x + 28, 16, 160, 30), Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
					x += 200;
				}
			}
			
			// News panel.
			Rectangle news = new Rectangle(40, 136, 560, 320);
			using (SolidBrush panel = new SolidBrush(Theme.Panel))
			{
				g.FillRectangle(panel, news);
			}
			using (Pen pen = new Pen(Theme.Border))
			{
				g.DrawRectangle(pen, news);
			}
			using (SolidBrush accent = new SolidBrush(Theme.Accent))
			{
				g.FillRectangle(accent, news.X, news.Y, 3, news.Height);
			}
			List<NewsItem> items = _feed == null ? null : (_showNotes ? _feed.patchnotes : _feed.news);
			int y = news.Y + 16;
			if ((items == null) || (items.Count == 0))
			{
				TextRenderer.DrawText(g, _feed == null ? "Загрузка новостей..." : "Пока пусто.", Font, new Point(news.X + 20, y), Theme.Muted);
			}
			else
			{
				using (Font head = new Font("Georgia", 15f, FontStyle.Bold))
				using (Font small = new Font("Segoe UI", 8.5f))
				{
					foreach (NewsItem item in items)
					{
						if (y > (news.Bottom - 40))
						{
							break;
						}
						// Reserve a separate column for the date; long news titles wrap instead of overlapping it.
						int dateWidth = string.IsNullOrEmpty(item.date) ? 0 : 145;
						int titleWidth = news.Width - 40 - dateWidth;
						Size titleSize = TextRenderer.MeasureText(g, item.title ?? "", head, new Size(titleWidth, 60), TextFormatFlags.WordBreak);
						int titleHeight = Math.Min(60, Math.Max(24, titleSize.Height));
						TextRenderer.DrawText(g, item.title ?? "", head, new Rectangle(news.X + 20, y, titleWidth, titleHeight), Theme.Text, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
						TextRenderer.DrawText(g, item.date ?? "", small, new Rectangle(news.Right - 150, y + 6, 135, 20), Theme.Muted, TextFormatFlags.Right);
						y += titleHeight + 8;
						Rectangle body = new Rectangle(news.X + 20, y, news.Width - 40, news.Bottom - y - 10);
						Size measured = TextRenderer.MeasureText(g, item.text ?? "", Font, new Size(body.Width, int.MaxValue), TextFormatFlags.WordBreak);
						TextRenderer.DrawText(g, item.text ?? "", Font, body, Color.FromArgb(215, 205, 190), TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
						y += Math.Min(measured.Height, body.Height) + 18;
					}
				}
			}
			
			// Bottom: version, status, progress, path.
			using (Font small = new Font("Segoe UI", 8.5f))
			using (Font status = new Font("Segoe UI", 11f, FontStyle.Bold))
			{
				TextRenderer.DrawText(g, _version.Length > 0 ? "ВЕРСИЯ " + _version : "", small, new Point(40, 520), Theme.Muted);
				TextRenderer.DrawText(g, _status.ToUpperInvariant(), status, new Rectangle(40, 538, 740, 24), Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
				Rectangle bar = new Rectangle(40, 568, 740, 4);
				using (SolidBrush track = new SolidBrush(Color.FromArgb(60, 255, 255, 255)))
				{
					g.FillRectangle(track, bar);
				}
				if (_total > 0)
				{
					using (SolidBrush fill = new SolidBrush(Theme.Accent))
					{
						g.FillRectangle(fill, bar.X, bar.Y, (int) ((bar.Width * Math.Min(_done, _total)) / _total), bar.Height);
					}
				}
				TextRenderer.DrawText(g, _rootText.ToUpperInvariant(), small, new Point(40, 586), Theme.Muted);
			}
		}

		private void CheckServers()
		{
			ThreadPool.QueueUserWorkItem(delegate
			{
				foreach (string[] server in _servers)
				{
					bool online = false;
					try
					{
						using (TcpClient client = new TcpClient())
						{
							IAsyncResult result = client.BeginConnect(server[1], int.Parse(server[2]), null, null);
							online = result.AsyncWaitHandle.WaitOne(2000) && client.Connected;
						}
					}
					catch (Exception)
					{
						online = false;
					}
					string name = server[0];
					bool value = online;
					Ui(() => { _online[name] = value; Invalidate(new Rectangle(580, 0, 520, 60)); });
				}
			});
		}

		private void LoadNews()
		{
			ThreadPool.QueueUserWorkItem(delegate
			{
				NewsFeed feed;
				try
				{
					using (WebClient client = new WebClient())
					{
						client.Encoding = Encoding.UTF8;
						feed = new JavaScriptSerializer().Deserialize<NewsFeed>(client.DownloadString(_newsUrl + "?t=" + DateTime.UtcNow.Ticks)) ?? new NewsFeed();
					}
				}
				catch (Exception)
				{
					feed = new NewsFeed();
				}
				Ui(() => { _feed = feed; Invalidate(); });
			});
		}

		private void Run(bool full)
		{
			if (_busy)
			{
				return;
			}
			if (Process.GetProcessesByName("L2").Length > 0)
			{
				_status = "Игра запущена: закройте L2 перед обновлением";
				Invalidate();
				return;
			}
			_busy = true;
			_main.Enabled = false;
			_main.Text = "ПРОВЕРКА...";
			ThreadPool.QueueUserWorkItem(delegate
			{
				string message;
				int outdatedCount = -1;
				string version = "";
				try
				{
					_updater.Status("Получение списка файлов...");
					Manifest manifest = _updater.ParseManifest(_updater.FetchManifest());
					version = manifest.version ?? "";
					List<ManifestFile> outdated = _updater.Compare(manifest, full);
					if (outdated.Count > 0)
					{
						Ui(() => _main.Text = "ЗАГРУЗКА...");
						_updater.Status("Доступно обновление: " + outdated.Count + " файлов");
						_updater.Download(outdated);
					}
					if (_updater.StartPendingSelfUpdate(true))
					{
						Ui(() => Application.Exit());
						return;
					}
					outdatedCount = 0;
					message = outdated.Count > 0 ? "Обновлено файлов: " + outdated.Count + ". Клиент готов к игре" : "Клиент актуален";
				}
				catch (WebException)
				{
					outdatedCount = 0;
					message = "Сервер обновлений недоступен. Можно играть на текущей версии";
				}
				catch (Exception e)
				{
					message = "Ошибка обновления: " + e.Message;
				}
				Ui(() =>
				{
					_status = message;
					_version = version;
					_outdated = outdatedCount;
					_total = 0;
					_main.Text = outdatedCount == 0 ? "ИГРАТЬ" : "ПОВТОРИТЬ";
					_main.Enabled = true;
					_busy = false;
					Invalidate();
				});
			});
		}

		private void Play()
		{
			if (!File.Exists(_gameExe))
			{
				_status = "Не найден " + _gameExe + ". Положите лаунчер в папку клиента";
				Invalidate();
				return;
			}
			Process.Start(new ProcessStartInfo(_gameExe) { WorkingDirectory = Path.GetDirectoryName(_gameExe), UseShellExecute = true });
			Close();
		}
	}

	public static class Program
	{
		public static Dictionary<string, string> ReadConfig(string file)
		{
			Dictionary<string, string> config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			if (File.Exists(file))
			{
				foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
				{
					int eq = line.IndexOf('=');
					if ((eq > 0) && !line.TrimStart().StartsWith("#"))
					{
						config[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
					}
				}
			}
			return config;
		}

		[STAThread]
		public static int Main(string[] args)
		{
			if (args.Length > 0 && args[0] == "--self-update") return SelfUpdate.RunHelper(args);
			Updater.EnableModernTls();
			string root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
			Dictionary<string, string> config = ReadConfig(Path.Combine(root, "launcher.ini"));
			if ((args.Length == 1) && (args[0] == "--update"))
			{
				// Console mode for tests and automation.
				Updater updater = new Updater(root, config.ContainsKey("ManifestUrl") ? config["ManifestUrl"] : "https://github.com/Sant0shiq/TalesUntold-updates/releases/latest/download/manifest.json");
				updater.Status = Console.WriteLine;
				Manifest manifest = updater.ParseManifest(updater.FetchManifest());
				List<ManifestFile> outdated = updater.Compare(manifest, false);
				updater.Download(outdated);
				if (updater.StartPendingSelfUpdate(false))
				{
					Console.WriteLine("PENDING self update; inspect .launcher-update/<token>/result.json");
					return SelfUpdate.PendingExitCode;
				}
				Console.WriteLine("OK " + manifest.version + " updated " + outdated.Count);
				return 0;
			}
			ServicePointManager.DefaultConnectionLimit = 8;
			Application.EnableVisualStyles();
			Application.Run(new LauncherForm(config, root));
			return 0;
		}
	}
}
