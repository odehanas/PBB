/*
 * GovTour - a tiny, dependency-free guided tour for GovBudget.
 *
 * Usage:
 *   GovTour.start([
 *     { el: '[data-tour="item"]', title: 'Item', text: '...', side: 'bottom' },
 *     { el: '#modal .body', title: '...', text: '...', before: fn, after: fn, delay: 500 }
 *   ], { onFinish: fn });
 *
 * Step options:
 *   el      (string|Element) target selector/element            [required]
 *   title   (string) heading text
 *   text    (string) body HTML
 *   side    'bottom'|'top'|'left'|'right'  preferred placement   [default 'bottom']
 *   padding (number) spotlight padding in px                     [default 6]
 *   before  (function) run when entering the step (e.g. open a modal)
 *   after   (function) run when leaving the step  (e.g. close a modal)
 *   delay   (number) ms to wait after `before` before positioning [default 0]
 *
 * If a step's target is missing at runtime it is skipped automatically.
 */
(function (window, document) {
    'use strict';

    var highlight, popover, titleEl, textEl, progEl, prevBtn, nextBtn, skipBtn;
    var steps = [], current = -1, opts = {}, active = false, rafId = null;

    function qs(sel) {
        if (!sel) return null;
        return (typeof sel === 'string') ? document.querySelector(sel) : sel;
    }

    function build() {
        if (highlight) return;

        highlight = document.createElement('div');
        highlight.className = 'govtour-highlight';
        highlight.style.display = 'none';

        popover = document.createElement('div');
        popover.className = 'govtour-popover';
        popover.style.display = 'none';
        popover.innerHTML =
            '<div class="govtour-title"></div>' +
            '<div class="govtour-text"></div>' +
            '<div class="govtour-foot">' +
            '<span class="govtour-progress"></span>' +
            '<div class="govtour-btns">' +
            '<button type="button" class="govtour-skip">Skip</button>' +
            '<button type="button" class="govtour-prev">Back</button>' +
            '<button type="button" class="govtour-next">Next</button>' +
            '</div></div>';

        document.body.appendChild(highlight);
        document.body.appendChild(popover);

        titleEl = popover.querySelector('.govtour-title');
        textEl = popover.querySelector('.govtour-text');
        progEl = popover.querySelector('.govtour-progress');
        prevBtn = popover.querySelector('.govtour-prev');
        nextBtn = popover.querySelector('.govtour-next');
        skipBtn = popover.querySelector('.govtour-skip');

        nextBtn.addEventListener('click', next);
        prevBtn.addEventListener('click', prev);
        skipBtn.addEventListener('click', function () { end(true); });
    }

    function render() {
        var s = steps[current];
        titleEl.textContent = s.title || '';
        textEl.innerHTML = s.text || '';
        progEl.textContent = (current + 1) + ' / ' + steps.length;
        prevBtn.style.display = current === 0 ? 'none' : '';
        nextBtn.textContent = current === steps.length - 1 ? 'Done' : 'Next';
    }

    function place() {
        var s = steps[current];
        var t = qs(s.el);
        if (!t) return;

        var pad = (s.padding != null) ? s.padding : 6;
        var r = t.getBoundingClientRect();

        highlight.style.display = 'block';
        highlight.style.top = (r.top - pad) + 'px';
        highlight.style.left = (r.left - pad) + 'px';
        highlight.style.width = (r.width + pad * 2) + 'px';
        highlight.style.height = (r.height + pad * 2) + 'px';

        popover.style.display = 'block';
        popover.style.visibility = 'hidden';
        var pw = popover.offsetWidth, ph = popover.offsetHeight;
        var vw = window.innerWidth, vh = window.innerHeight;
        var gap = pad + 12;
        var side = s.side || 'bottom';
        var top, left;

        function compute(sd) {
            if (sd === 'bottom') { top = r.bottom + gap; left = r.left + r.width / 2 - pw / 2; }
            else if (sd === 'top') { top = r.top - gap - ph; left = r.left + r.width / 2 - pw / 2; }
            else if (sd === 'right') { left = r.right + gap; top = r.top + r.height / 2 - ph / 2; }
            else if (sd === 'left') { left = r.left - gap - pw; top = r.top + r.height / 2 - ph / 2; }
        }

        compute(side);
        if (side === 'bottom' && top + ph > vh - 8) compute('top');
        else if (side === 'top' && top < 8) compute('bottom');
        if (side === 'right' && left + pw > vw - 8) compute('left');
        else if (side === 'left' && left < 8) compute('right');

        left = Math.max(8, Math.min(left, vw - pw - 8));
        top = Math.max(8, Math.min(top, vh - ph - 8));
        popover.style.top = top + 'px';
        popover.style.left = left + 'px';
        popover.style.visibility = 'visible';
    }

    function leave(i) {
        var s = steps[i];
        if (s && typeof s.after === 'function') { try { s.after(); } catch (e) { } }
    }

    function showAt(n) {
        if (!active) return;
        if (n < 0) n = 0;
        if (n >= steps.length) { end(false); return; }
        if (current >= 0 && current !== n) leave(current);
        current = n;

        var s = steps[current];
        if (typeof s.before === 'function') { try { s.before(); } catch (e) { } }

        setTimeout(function () {
            if (!active) return;
            var t = qs(s.el);
            if (!t) { showAt(current + 1); return; } // skip missing targets
            render();
            try { t.scrollIntoView({ behavior: 'smooth', block: 'center', inline: 'nearest' }); } catch (e) { }
            setTimeout(place, 260);
        }, s.delay || 0);
    }

    function next() { showAt(current + 1); }
    function prev() { showAt(current - 1); }

    function onReposition() {
        if (!active) return;
        if (rafId) cancelAnimationFrame(rafId);
        rafId = requestAnimationFrame(place);
    }

    function onKey(e) {
        if (!active) return;
        if (e.key === 'Escape') end(true);
        else if (e.key === 'ArrowRight') next();
        else if (e.key === 'ArrowLeft') prev();
    }

    function start(stepList, options) {
        steps = (stepList || []).slice();
        opts = options || {};
        if (!steps.length) return;
        build();
        active = true;
        current = -1;
        window.addEventListener('resize', onReposition, true);
        window.addEventListener('scroll', onReposition, true);
        document.addEventListener('keydown', onKey, true);
        showAt(0);
    }

    function end(skipped) {
        if (current >= 0) leave(current);
        active = false;
        current = -1;
        if (highlight) highlight.style.display = 'none';
        if (popover) popover.style.display = 'none';
        window.removeEventListener('resize', onReposition, true);
        window.removeEventListener('scroll', onReposition, true);
        document.removeEventListener('keydown', onKey, true);
        if (opts && typeof opts.onFinish === 'function') {
            try { opts.onFinish(!!skipped); } catch (e) { }
        }
    }

    window.GovTour = { start: start, end: end, get active() { return active; } };

})(window, document);
