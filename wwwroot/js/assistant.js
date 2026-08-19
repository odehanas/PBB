// Floating budget assistant. Posts questions to /Chat/Ask and renders the reply.
(function () {
    'use strict';

    var widget = document.getElementById('assistantWidget');
    if (!widget) return;

    var panel = document.getElementById('assistantPanel');
    var log = document.getElementById('assistantLog');
    var form = document.getElementById('assistantForm');
    var input = document.getElementById('assistantInput');
    var sendBtn = document.getElementById('assistantSend');
    var askUrl = widget.getAttribute('data-ask-url');
    var resetUrl = widget.getAttribute('data-reset-url');
    var token = widget.getAttribute('data-token');
    var busy = false;

    function escapeHtml(text) {
        var div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    // Minimal markdown: pipe tables, bold, inline code, bullets and line breaks.
    function render(text) {
        var lines = escapeHtml(text).split('\n');
        var html = '';
        var i = 0;

        while (i < lines.length) {
            var line = lines[i];
            var isTableRow = /^\s*\|.*\|\s*$/.test(line);
            var isSeparator = i + 1 < lines.length && /^\s*\|[\s:|-]+\|\s*$/.test(lines[i + 1]);

            if (isTableRow && isSeparator) {
                var cells = function (row) {
                    return row.trim().replace(/^\||\|$/g, '').split('|').map(function (c) { return c.trim(); });
                };
                html += '<table><thead><tr>' + cells(line).map(function (c) {
                    return '<th>' + c + '</th>';
                }).join('') + '</tr></thead><tbody>';
                i += 2;
                while (i < lines.length && /^\s*\|.*\|\s*$/.test(lines[i])) {
                    html += '<tr>' + cells(lines[i]).map(function (c) { return '<td>' + c + '</td>'; }).join('') + '</tr>';
                    i++;
                }
                html += '</tbody></table>';
                continue;
            }

            html += line + '\n';
            i++;
        }

        return html
            .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
            .replace(/`([^`]+)`/g, '<code>$1</code>')
            .replace(/\n/g, '<br>');
    }

    function append(text, cssClass, asHtml) {
        var div = document.createElement('div');
        div.className = 'assistant-msg ' + cssClass;
        if (asHtml) {
            div.innerHTML = render(text);
        } else {
            div.textContent = text;
        }
        log.appendChild(div);
        log.scrollTop = log.scrollHeight;
        return div;
    }

    function setBusy(state) {
        busy = state;
        sendBtn.disabled = state;
        input.disabled = state;
    }

    function ask(question) {
        if (busy || !question) return;
        append(question, 'assistant-msg-user', false);
        input.value = '';
        setBusy(true);

        var pending = append('Thinking', 'assistant-msg-bot assistant-typing', false);

        fetch(askUrl, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token },
            body: JSON.stringify({ message: question })
        }).then(function (response) {
            if (!response.ok) throw new Error('HTTP ' + response.status);
            return response.json();
        }).then(function (data) {
            pending.remove();
            append(data.reply, data.ok ? 'assistant-msg-bot' : 'assistant-msg-error', true);
        }).catch(function () {
            pending.remove();
            append('The assistant is unavailable right now. Please try again.', 'assistant-msg-error', false);
        }).finally(function () {
            setBusy(false);
            input.focus();
        });
    }

    document.getElementById('assistantLauncher').addEventListener('click', function () {
        var open = panel.hasAttribute('hidden');
        if (open) {
            panel.removeAttribute('hidden');
            input.focus();
        } else {
            panel.setAttribute('hidden', '');
        }
        this.setAttribute('aria-expanded', open ? 'true' : 'false');
    });

    document.getElementById('assistantClose').addEventListener('click', function () {
        panel.setAttribute('hidden', '');
        document.getElementById('assistantLauncher').setAttribute('aria-expanded', 'false');
    });

    document.getElementById('assistantReset').addEventListener('click', function () {
        fetch(resetUrl, { method: 'POST', headers: { 'RequestVerificationToken': token } })
            .finally(function () {
                log.innerHTML = '';
                append('Conversation cleared. Ask me anything about your budget or OECD performance budgeting.', 'assistant-msg-bot', false);
            });
    });

    log.addEventListener('click', function (event) {
        var suggestion = event.target.closest('.assistant-suggestion');
        if (suggestion) ask(suggestion.textContent.trim());
    });

    form.addEventListener('submit', function (event) {
        event.preventDefault();
        ask(input.value.trim());
    });
})();
