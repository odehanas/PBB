// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.
(function () {
  function applySidebarState(hidden) {
    document.body.classList.toggle('sidebar-hidden', hidden);

    var btn = document.getElementById('appSidebarToggle');
    if (btn) {
      btn.setAttribute('aria-pressed', hidden ? 'true' : 'false');
    }
  }

  function resolveTheme(setting) {
    if (setting === 'dark' || setting === 'light') return setting;
    return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  }

  function applyThemeSetting(setting) {
    var resolved = resolveTheme(setting);
    document.documentElement.setAttribute('data-bs-theme', resolved);
    document.documentElement.setAttribute('data-app-theme', setting);

    var btn = document.getElementById('appThemeToggle');
    if (btn) {
      btn.textContent = resolved === 'dark' ? 'Light' : 'Dark';
    }
  }

  document.addEventListener('DOMContentLoaded', function () {
    var stored = localStorage.getItem('app_sidebar_hidden');
    var hidden = stored === '1';
    applySidebarState(hidden);

    var btn = document.getElementById('appSidebarToggle');
    if (!btn) return;

    btn.addEventListener('click', function () {
      hidden = !document.body.classList.contains('sidebar-hidden');
      localStorage.setItem('app_sidebar_hidden', hidden ? '1' : '0');
      applySidebarState(hidden);
    });

    var themeStored = localStorage.getItem('app_theme') || 'system';
    applyThemeSetting(themeStored);

    var themeBtn = document.getElementById('appThemeToggle');
    if (themeBtn) {
      themeBtn.addEventListener('click', function () {
        var currentResolved = document.documentElement.getAttribute('data-bs-theme') || resolveTheme('system');
        var next = currentResolved === 'dark' ? 'light' : 'dark';
        localStorage.setItem('app_theme', next);
        applyThemeSetting(next);
      });
    }

    if (window.matchMedia) {
      var mq = window.matchMedia('(prefers-color-scheme: dark)');
      if (mq && mq.addEventListener) {
        mq.addEventListener('change', function () {
          var setting = localStorage.getItem('app_theme') || 'system';
          if (setting === 'system') applyThemeSetting(setting);
        });
      }
    }

    // Seed the set of already-loaded external scripts from the initial page so
    // boosted navigations only load NEW external scripts once (and never re-run
    // library code that would collide on top-level declarations).
    seedLoadedScripts();
  });

  // ---------------------------------------------------------------------------
  // Phase 3 — htmx boosted navigation (swaps #appContent only)
  //   * top progress bar while a boosted request is in flight
  //   * re-run the swapped page's scripts safely (htmx has allowScriptTags:false):
  //       - inline scripts run in an isolated function scope so top-level
  //         const/let/function declarations can't collide on revisits
  //       - external <script src> are loaded once (deduped)
  //   * scroll back to top after content is swapped in
  // ---------------------------------------------------------------------------
  var loadedScriptSrc = null;

  function seedLoadedScripts() {
    loadedScriptSrc = {};
    var scripts = document.querySelectorAll('script[src]');
    for (var i = 0; i < scripts.length; i++) {
      loadedScriptSrc[scripts[i].src] = true; // .src is the resolved absolute URL
    }
  }

  function runSwappedScripts(container) {
    if (!container) return;
    if (!loadedScriptSrc) seedLoadedScripts();

    var scripts = container.querySelectorAll('script');
    scripts.forEach(function (old) {
      var src = old.getAttribute('src');
      if (src) {
        var abs;
        try { abs = new URL(src, document.baseURI).href; } catch (e) { abs = src; }
        if (loadedScriptSrc[abs]) return; // already loaded globally — don't re-run
        loadedScriptSrc[abs] = true;
        var s = document.createElement('script');
        for (var i = 0; i < old.attributes.length; i++) {
          var a = old.attributes[i];
          s.setAttribute(a.name, a.value);
        }
        document.body.appendChild(s);
      } else {
        var code = old.textContent || '';
        if (!code.trim()) return;
        try {
          (new Function(code)).call(window);
        } catch (e) {
          if (window.console) console.error('Error running swapped script:', e);
        }
      }
    });
  }

  document.addEventListener('htmx:beforeRequest', function () {
    document.body.classList.add('app-loading');
  });

  function endLoading() {
    document.body.classList.remove('app-loading');
  }
  document.addEventListener('htmx:afterRequest', endLoading);
  document.addEventListener('htmx:responseError', endLoading);
  document.addEventListener('htmx:sendError', endLoading);
  document.addEventListener('htmx:timeout', endLoading);

  document.addEventListener('htmx:afterSwap', function () {
    var content = document.getElementById('appContent');
    runSwappedScripts(content);
    try { window.scrollTo({ top: 0, behavior: 'auto' }); } catch (e) { window.scrollTo(0, 0); }
  });
})();
