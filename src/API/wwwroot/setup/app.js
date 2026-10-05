/*
 * The setup app.
 *
 * Plain HTML, CSS and JavaScript on purpose. No framework, no build step and nothing
 * fetched from a CDN, because this is the tool you reach for when things are not working,
 * possibly at a venue with no usable internet. It has to work from the file the API
 * already serves.
 *
 * It also deliberately polls instead of using SignalR: the hub's JavaScript client would
 * be another script to have on hand, and a 500ms poll of two tiny endpoints costs nothing.
 */

const api = async (path, options) => {
    const response = await fetch(path, options);
    const text = await response.text();
    const body = text ? JSON.parse(text) : null;
    if (!response.ok) throw new Error(body?.detail ?? body?.message ?? `${response.status} ${response.statusText}`);
    return body;
};

const post = (path) => api(path, { method: 'POST' });

const postJson = (path, payload) => api(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload)
});

const el = (id) => document.getElementById(id);
const escape = (value) => String(value ?? '').replace(/[&<>"']/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

// ---------------------------------------------------------------- tabs

// The tab is in the URL, so /setup/#control can be bookmarked on the phone and land
// straight on the trigger button rather than two taps away.
const showPanel = (name) => {
    const tabs = [...document.querySelectorAll('.tab')];
    const tab = tabs.find((t) => t.dataset.panel === name) ?? tabs[0];

    tabs.forEach((t) => t.classList.toggle('active', t === tab));
    document.querySelectorAll('.panel').forEach((p) =>
        p.classList.toggle('active', p.id === `panel-${tab.dataset.panel}`));
};

document.querySelectorAll('.tab').forEach((tab) => {
    tab.addEventListener('click', () => {
        location.hash = tab.dataset.panel;
        showPanel(tab.dataset.panel);
    });
});

window.addEventListener('hashchange', () => showPanel(location.hash.slice(1)));
showPanel(location.hash.slice(1));

// ---------------------------------------------------------------- checks

const renderChecks = (report) => {
    el('checks-summary').textContent = report.ready
        ? `Ready${report.warnings ? `, with ${report.warnings} warning(s)` : ''} — checked in ${(report.elapsedMs / 1000).toFixed(1)}s`
        : `Not ready: ${report.failures} failing${report.warnings ? `, ${report.warnings} warning(s)` : ''}`;

    el('checks').innerHTML = report.checks.map((check) => `
        <li class="${escape(check.status)}">
            <div class="head">
                <span class="name">${escape(check.name)}</span>
                <span class="badge">${escape(check.status)}</span>
                ${check.elapsedMs > 50 ? `<span class="elapsed">${(check.elapsedMs / 1000).toFixed(1)}s</span>` : ''}
            </div>
            <p class="detail">${escape(check.detail)}</p>
            ${check.remediation ? `<p class="fix">${escape(check.remediation)}</p>` : ''}
            ${check.action ? `<div class="fix-row"><button data-action="${escape(check.action)}">Fix it</button></div>` : ''}
        </li>`).join('');

    el('checks').querySelectorAll('button[data-action]').forEach((button) => {
        button.addEventListener('click', () => runAction(button.dataset.action, button));
    });
};

const runChecks = async () => {
    const button = el('run-checks');
    button.disabled = true;
    el('checks-summary').textContent = 'Checking… (the model and TTS probes also warm them up)';

    try {
        renderChecks(await api('/api/setup/readiness'));
    } catch (error) {
        el('checks-summary').textContent = `Could not run the checks: ${error.message}`;
    } finally {
        button.disabled = false;
    }
};

const runAction = async (id, button) => {
    button.disabled = true;
    button.textContent = 'Working…';

    try {
        const result = await post(`/api/setup/actions/${encodeURIComponent(id)}`);
        log(`${id}: ${result.detail}`);
    } catch (error) {
        log(`${id} failed: ${error.message}`);
    }

    // Re-run rather than assume: the fix either worked or it did not.
    await runChecks();
};

el('run-checks').addEventListener('click', runChecks);

// The end-to-end test. Awaited rather than fired and forgotten, because a failure here is
// exactly what you want reported: the request returns once the line has finished playing.
el('test-speech').addEventListener('click', async () => {
    const button = el('test-speech');
    const result = el('test-result');

    button.disabled = true;
    button.textContent = 'Speaking… watch the display';
    result.className = 'muted small';
    result.textContent = '';

    try {
        const response = await postJson('/api/setup/test-speech', { text: el('test-text').value });
        result.className = 'small ok';
        result.textContent = response.message;
        log(`test speech: ${response.message}`);
    } catch (error) {
        result.className = 'small bad';
        result.textContent = error.message;
        log(`test speech failed: ${error.message}`);
    } finally {
        button.disabled = false;
        button.textContent = 'Say something and watch the display';
    }
});

// ---------------------------------------------------------------- inputs

let devices = [];
let selectedIds = new Set();

const loadDevices = async () => {
    const [all, selected] = await Promise.all([
        api('/api/devices'),
        api('/api/devices/selected')
    ]);

    devices = all;
    selectedIds = new Set(selected.map((d) => d.id));
    renderDevices();
};

const renderDevices = () => {
    el('devices').innerHTML = devices.map((device) => `
        <li data-id="${escape(device.id)}">
            <div class="top">
                <input type="checkbox" ${selectedIds.has(device.id) ? 'checked' : ''}
                       aria-label="Capture from this device">
                <input type="text" value="${escape(device.displayName ?? '')}"
                       placeholder="${escape(device.name)}" aria-label="Speaker name">
            </div>
            <div class="meter">
                <div class="fill" style="width:0"></div>
                <div class="peak" style="left:0"></div>
                <div class="threshold" style="left:0"></div>
            </div>
            <div class="node">${escape(device.name)}</div>
        </li>`).join('');

    el('devices').querySelectorAll('li').forEach((item) => {
        const id = item.dataset.id;

        item.querySelector('input[type=checkbox]').addEventListener('change', (e) => {
            if (e.target.checked) selectedIds.add(id); else selectedIds.delete(id);
        });

        // Saved on blur rather than per keystroke: the name goes straight into the
        // transcript, and a half-typed name there is worse than a late one.
        item.querySelector('input[type=text]').addEventListener('change', async (e) => {
            try {
                await postJson(`/api/devices/${encodeURIComponent(id)}/rename`, { displayName: e.target.value });
                log(`Named ${id} "${e.target.value}"`);
            } catch (error) {
                log(`Could not rename: ${error.message}`);
            }
        });
    });
};

/**
 * The level meters. A linear bar would spend its whole life near zero, since speech RMS
 * sits well under 0.1, so this is scaled the way a meter is: by how loud it sounds.
 */
const meterWidth = (rms) => {
    if (!(rms > 0)) return 0;
    const db = 20 * Math.log10(rms);
    return Math.max(0, Math.min(100, ((db + 60) / 60) * 100));
};

const updateLevels = async () => {
    let data;
    try {
        data = await api('/api/setup/levels');
    } catch {
        return;
    }

    const displays = el('displays');
    displays.className = `state ${data.displays > 0 ? 'state-displays' : 'state-nodisplays'}`;
    el('displays-text').textContent = `${data.displays} display${data.displays === 1 ? '' : 's'}`;

    const byId = new Map(data.devices.map((d) => [d.deviceId, d]));
    const thresholdLeft = meterWidth(data.silenceThreshold);

    el('devices').querySelectorAll('li').forEach((item) => {
        const level = byId.get(item.dataset.id);
        const fill = item.querySelector('.fill');
        const peak = item.querySelector('.peak');
        item.querySelector('.threshold').style.left = `${thresholdLeft}%`;

        if (!level) {
            fill.style.width = '0';
            peak.style.left = '0';
            return;
        }

        fill.style.width = `${meterWidth(level.rms)}%`;
        fill.classList.toggle('quiet', level.rms < data.silenceThreshold);
        peak.style.left = `${meterWidth(level.peak)}%`;
    });
};

el('refresh-devices').addEventListener('click', () => loadDevices().catch((e) => log(e.message)));

el('save-selection').addEventListener('click', async () => {
    try {
        const result = await postJson('/api/devices/select', { deviceIds: [...selectedIds] });
        log(result.message);
        await loadDevices();
    } catch (error) {
        log(`Could not set the capture devices: ${error.message}`);
    }
});

// ---------------------------------------------------------------- control

const log = (message) => {
    const line = `${new Date().toLocaleTimeString()}  ${message}\n`;
    el('control-log').textContent = line + el('control-log').textContent;
};

const command = (id, path) => el(id).addEventListener('click', async () => {
    try {
        const result = await post(path);
        log(result?.message ?? `${path}: ok`);
    } catch (error) {
        log(`${path} failed: ${error.message}`);
    }
    await updateState();
});

command('trigger', '/api/panelist/trigger');
command('cancel', '/api/panelist/cancel');
command('introduce', '/api/panelist/introduce');
command('enable', '/api/panelist/enable');
command('disable', '/api/panelist/disable');

const updateState = async () => {
    const badge = el('state');
    const text = el('state-text');

    try {
        const { state, isDisabled } = await api('/api/panelist/state');
        badge.className = `state state-${state}`;
        text.textContent = isDisabled ? `${state} (disabled)` : state;
    } catch {
        badge.className = 'state state-offline';
        text.textContent = 'API unreachable';
    }
};

// ---------------------------------------------------------------- this run

const yesNo = (on) => `<span class="${on ? 'on' : 'off'}">${on ? 'on' : 'off'}</span>`;

const loadSummary = async () => {
    const s = await api('/api/setup/summary');

    el('summary').innerHTML = `
        <h2>Services</h2>
        <dl>
            <dt>Speech to text</dt><dd>${escape(s.services.stt)}</dd>
            <dt>Language model</dt><dd>${escape(s.services.llm)}${s.model ? ` — ${escape(s.model)}` : ''}</dd>
            <dt>Endpoint</dt><dd>${escape(s.endpoint ?? '(default)')}</dd>
            <dt>Text to speech</dt><dd>${escape(s.services.tts)}</dd>
            <dt>Audio devices</dt><dd>${escape(s.services.audioDevice)}</dd>
            <dt>Audio playback</dt><dd>${escape(s.services.audioPlayback)}</dd>
        </dl>
        <h2>Audio graph</h2>
        <dl>
            <dt>Capture source</dt><dd>${escape(s.audio.captureNode ?? '(system default)')}</dd>
            <dt>Output sink</dt><dd>${escape(s.audio.outputSink ?? '(system default)')}</dd>
        </dl>
        <h2>Features</h2>
        <dl>
            <dt>Streamed answers</dt><dd>${yesNo(s.features.streaming)}</dd>
            <dt>Spoken triggers</dt><dd>${yesNo(s.features.spokenTriggers)}</dd>
            <dt>Transcript search</dt><dd>${yesNo(s.features.transcriptSearch)}</dd>
            <dt>Response triage</dt><dd>${yesNo(s.features.responseTriage)}</dd>
            <dt>Silence guard</dt><dd>${yesNo(s.features.silenceGuard)}</dd>
            <dt>Mouth envelope</dt><dd>${yesNo(s.features.speechEnvelope)}</dd>
        </dl>
        ${s.triggerPhrases.length ? `<h2>Trigger phrases</h2><ul>${
            s.triggerPhrases.map((p) => `<li>“${escape(p)}”</li>`).join('')}</ul>` : ''}`;
};

// ---------------------------------------------------------------- start

updateState();
loadDevices().catch((e) => log(e.message));
loadSummary().catch((e) => log(e.message));

setInterval(updateState, 1000);
setInterval(updateLevels, 250);
