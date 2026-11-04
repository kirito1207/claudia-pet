using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Claudia
{
    /// <summary>根据屏幕工作区限制设置窗口高度</summary>
    static class WindowSize
    {
        public static double FitHeight(double h) => Math.Min(h, SystemParameters.WorkArea.Height - 40);
    }

    /// <summary>
    /// 设置窗口（右键「设置…」）：加载 web/settings.html，
    /// 页面通过 chrome.webview.postMessage 发 {cmd, ...}，Pet.HandleSettings 改完设置后用 Push 把最新状态推回页面
    /// </summary>
    sealed class SettingsWindow
    {
        readonly Pet pet;
        readonly Window window;
        readonly WebView2 web;
        public event Action Closed;

        /// <summary>page：设置页 settings.html</summary>
        public SettingsWindow(Pet pet, CoreWebView2Environment env, string page = "settings.html", string title = Store.AppName + " 设置", double width = 560, double height = 780)
        {
            this.pet = pet;
            web = new WebView2();
            window = new Window { Title = title, Width = width, Height = WindowSize.FitHeight(height), MinWidth = 420, Content = web, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            window.Closed += (_, __) => Closed?.Invoke();
            window.Loaded += async (_, __) =>
            {
                await web.EnsureCoreWebView2Async(env);
                Pet.Map(web.CoreWebView2);
                web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                web.CoreWebView2.WebMessageReceived += (_, e) =>
                {
                    if (Store.Json.DeserializeObject(e.WebMessageAsJson) is Dictionary<string, object> msg)
                        try { pet.HandleSettings(msg); } catch (Exception ex) { Store.Log("设置出错: " + ex); }
                };
                web.CoreWebView2.NavigationCompleted += (_, __) =>
                {
                    loaded = true;
                    if (pendingTab != null) { var t = pendingTab; pendingTab = null; ShowTab(t); }
                };
                web.CoreWebView2.Navigate(Store.PageUrl(page));
            };
        }

        bool loaded; string pendingTab;
        /// <summary>切到某个标签页（「新功能」卡片的「去看看」）；页面还没加载完就等加载完再切</summary>
        public void ShowTab(string tab)
        {
            if (!loaded || web.CoreWebView2 == null) { pendingTab = tab; return; }
            try { web.CoreWebView2.ExecuteScriptAsync($"showTab({Store.Json.Serialize(tab)})"); } catch { }
        }

        public void Show() { window.Show(); window.Activate(); }
        public Size Size => new Size(window.ActualWidth, window.ActualHeight);
        public void Push()
        {
            try { web.CoreWebView2?.ExecuteScriptAsync($"setState({Store.Json.Serialize(pet.SettingsState())})"); } catch { }
        }
    }

}
