// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
//
// This file is part of MarkPortO.
//
// MarkPortO is free software: you can redistribute it and/or modify it under the terms of
// the GNU General Public License, version 3 only, as published by the Free Software Foundation.
//
// MarkPortO is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with MarkPortO.
// If not, see <https://www.gnu.org/licenses/gpl-3.0.html>.

using System.IO;
using System.Windows;
using System.Windows.Controls;
using MarkPortO.Core.Markdown;
using Microsoft.Web.WebView2.Core;

namespace MarkPortO.App;

/// <summary>One pane of the preview that shows Markdown (FR-068): drawn by WebView2, or as written.</summary>
public partial class MarkdownPane : UserControl
{
    private bool _ready;
    private string? _mappedFolder;

    public MarkdownPane()
    {
        InitializeComponent();
        RightDragScroll.Attach(RawBox);
    }

    // Scrolls the page with the right mouse button, like the other panes: the content follows the pointer.
    private const string DragScrollScript = """
        (function () {
          var x = 0, y = 0, down = false;
          document.addEventListener('mousedown', function (e) { if (e.button === 2) { down = true; x = e.screenX; y = e.screenY; e.preventDefault(); } });
          document.addEventListener('mousemove', function (e) {
            if (!down) { return; }
            if ((e.buttons & 2) === 0) { down = false; return; }
            window.scrollBy(x - e.screenX, y - e.screenY);
            x = e.screenX; y = e.screenY;
          });
          document.addEventListener('mouseup', function (e) { if (e.button === 2) { down = false; } });
          document.addEventListener('contextmenu', function (e) { e.preventDefault(); });

          // The wheel scrolls the page; at the top or the bottom (after a short pause) it asks for the page before or after. The arrow keys ask too.
          var last = 0;
          function post(message) { try { window.chrome.webview.postMessage(message); } catch (error) { } }
          function atEdge(down) {
            var max = document.documentElement.scrollHeight - window.innerHeight;
            return down ? window.scrollY >= max - 1 : window.scrollY <= 0;
          }
          document.addEventListener('wheel', function (e) {
            if (e.ctrlKey || e.deltaY === 0) { return; }
            var down = e.deltaY > 0, now = Date.now();
            if (!atEdge(down)) { last = now; return; }
            e.preventDefault();
            if (now - last > 400) { last = now; post(down ? 'next' : 'previous'); }
          }, { passive: false });
          // A Mermaid block is drawn as a picture. The library is loaded from the folder of the program (the page allows only that one script);
          // a block that cannot be drawn stays as it is written.
          document.addEventListener('DOMContentLoaded', function () {
            // Markdig writes a mermaid block as <pre class="mermaid">.
            var blocks = document.querySelectorAll('pre.mermaid');
            if (blocks.length === 0) { return; }
            var script = document.createElement('script');
            script.src = '@MERMAID@';
            script.onload = function () {
              try {
                mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', theme: window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'default' });
              } catch (error) { return; }
              blocks.forEach(function (code, index) {
                var pre = code;
                mermaid.render('mermaid-' + index, code.textContent).then(function (result) {
                  var figure = document.createElement('div');
                  figure.className = 'mermaid-figure';
                  figure.innerHTML = result.svg;
                  pre.parentNode.replaceChild(figure, pre);
                }).catch(function () {
                  var leftover = document.getElementById('dmermaid-' + index);
                  if (leftover) { leftover.remove(); }
                });
              });
            };
            document.head.appendChild(script);
          });
          document.addEventListener('keydown', function (e) {
            if (e.altKey || e.ctrlKey || e.shiftKey) { return; }
            if (e.key === 'ArrowRight') { post('next'); e.preventDefault(); }
            else if (e.key === 'ArrowLeft') { post('previous'); e.preventDefault(); }
          });
        })();
        """;

    public string Header
    {
        get => HeaderText.Text;
        set => HeaderText.Text = value;
    }

    /// <summary>The page shown asks for the page after it (1) or before it (-1): the wheel at the end of the page, or an arrow key.</summary>
    public event Action<int>? PageStepRequested;

    /// <summary>True when the WebView2 runtime is not there, so the Markdown can only be shown as written.</summary>
    public bool WebFailed { get; private set; }

    public async Task InitialiseAsync()
    {
        try
        {
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkPortO", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(null, data);
            await Web.EnsureCoreWebView2Async(environment);
            var settings = Web.CoreWebView2.Settings;
            // The pages carry a policy that allows no script of their own (MarkdownHtml); only the one script below, put in by the program, runs.
            settings.IsScriptEnabled = true;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            // The only message that the page script sends is "next" or "previous" (the page itself cannot run script).
            settings.IsWebMessageEnabled = true;
            Web.CoreWebView2.WebMessageReceived += (_, args) =>
            {
                switch (args.TryGetWebMessageAsString())
                {
                    case "next":
                        PageStepRequested?.Invoke(1);
                        break;
                    case "previous":
                        PageStepRequested?.Invoke(-1);
                        break;
                }
            };
            // Nothing is opened from the page: no link, no new window.
            Web.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (!(args.Uri.StartsWith("data:", StringComparison.Ordinal) || args.Uri.StartsWith("about:", StringComparison.Ordinal)))
                {
                    args.Cancel = true;
                }
            };
            Web.CoreWebView2.NewWindowRequested += (_, args) => args.Handled = true;
            // The library of the diagrams is in the folder of the program; the pages may load that one file (see MarkdownHtml).
            var mermaidFolder = Path.Combine(AppContext.BaseDirectory, "Assets", "mermaid");
            if (Directory.Exists(mermaidFolder))
            {
                Web.CoreWebView2.SetVirtualHostNameToFolderMapping(MarkdownHtml.MermaidHost, mermaidFolder, CoreWebView2HostResourceAccessKind.Allow);
            }

            await Web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(DragScrollScript.Replace("@MERMAID@", MarkdownHtml.MermaidScript, StringComparison.Ordinal));
            _ready = true;
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            // Without the WebView2 runtime the Markdown is shown as text.
            WebFailed = true;
            WebNote.Visibility = Visibility.Visible;
        }
    }

    public void Show(string markdown, string folder, bool rendered)
    {
        RawBox.Text = markdown;
        if (!rendered || WebFailed)
        {
            RawBox.Visibility = Visibility.Visible;
            Web.Visibility = Visibility.Collapsed;
            return;
        }

        RawBox.Visibility = Visibility.Collapsed;
        Web.Visibility = Visibility.Visible;
        if (!_ready)
        {
            return;
        }

        // The pictures of the Markdown are found under this name; the files are in the folder of the document.
        if (_mappedFolder != folder)
        {
            if (_mappedFolder is not null)
            {
                Web.CoreWebView2.ClearVirtualHostNameToFolderMapping(MarkdownHtml.Host);
            }

            Web.CoreWebView2.SetVirtualHostNameToFolderMapping(MarkdownHtml.Host, folder, CoreWebView2HostResourceAccessKind.Allow);
            _mappedFolder = folder;
        }

        Web.NavigateToString(MarkdownHtml.Render(markdown));
    }

    public void Release() => Web.Dispose();
}
