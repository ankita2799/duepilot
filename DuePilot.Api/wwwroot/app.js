const $ = id => document.getElementById(id);

const EXAMPLES = {
    ask: [
        'Who owes me the most money?',
        'Which invoices are more than 30 days late?',
        'Show me unsent invoices',
        'What is due in the next 7 days?'
    ],
    chase: [
        'Chase the 3 most overdue invoices',
        'Send out all invoices that are still drafts',
        'Remind clients who are more than 30 days late'
    ]
};
const mode = () => document.querySelector('input[name="mode"]:checked').value;

function renderExamples() {
    $('examples').innerHTML = EXAMPLES[mode()]
        .map(e => `<button type="button" class="btn btn-sm btn-outline-secondary me-1 mb-1">${esc(e)}</button>`).join('');
}

// Escape anything that came from the database or the AI before putting it in the page
function esc(v) {
    return String(v ?? '').replace(/[&<>"']/g,
        c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}
function currentRole() { return $('role').value; }

async function api(path, options = {}) {
    try {
        const res = await fetch(path, {
            ...options,
            headers: { 'Content-Type': 'application/json', 'X-Role': currentRole(), ...(options.headers || {}) }
        });
        let data = null;
        try { data = await res.json(); } catch { /* empty body */ }
        return { ok: res.ok, status: res.status, data };
    } catch {
        return { ok: false, status: 0, data: { error: 'Could not reach the server. Is it running?' } };
    }
}

const money = n => '$' + Number(n).toLocaleString('en-US', { maximumFractionDigits: 2 });

const LABELS = {
    invoice_number: 'Invoice', invoice_id: 'ID', client_name: 'Client', client_email: 'Email',
    days_overdue: 'Days late', due_date: 'Due', issued_date: 'Issued', last_reminder_date: 'Last reminder'
};
const label = k => LABELS[k] || k.replace(/_/g, ' ').replace(/^./, c => c.toUpperCase());

const STATUS_BADGE = { Draft: 'secondary', Sent: 'primary', Paid: 'success', Cancelled: 'dark' };

function cell(key, v) {
    if (v === null || v === undefined || v === '') return '<span class="text-muted">-</span>';
    const k = key.toLowerCase();
    if (typeof v === 'number' && /amount|total|sum|balance|owed|value/.test(k)) return money(v);
    if (k === 'days_overdue') {
        const d = Number(v);
        return `<span class="badge text-bg-${d > 30 ? 'danger' : d > 0 ? 'warning' : 'secondary'}">${d}</span>`;
    }
    if (k === 'status') return `<span class="badge text-bg-${STATUS_BADGE[v] || 'light'}">${esc(v)}</span>`;
    return esc(v);
}

function table(rows) {
    if (!rows.length) return '<p class="text-muted mb-0">No matching invoices.</p>';
    const cols = Object.keys(rows[0]);
    const head = cols.map(c => `<th>${esc(label(c))}</th>`).join('');
    const body = rows.map(r => '<tr>' + cols.map(c => `<td>${cell(c, r[c])}</td>`).join('') + '</tr>').join('');
    return `<div class="table-wrap"><table class="table table-sm table-hover align-middle mb-0">
            <thead><tr>${head}</tr></thead><tbody>${body}</tbody></table></div>`;
}

function showResult(title, sql, bodyHtml) {
    $('resultTitle').textContent = title;
    $('resultBody').innerHTML = bodyHtml;
    $('sqlText').textContent = sql || '';
    $('sqlToggle').classList.toggle('d-none', !sql);
    if (!sql) bootstrap.Collapse.getOrCreateInstance($('sqlPanel'), { toggle: false }).hide();
    $('resultCard').classList.remove('d-none');
}

let busy = false;

async function ask() {
    const q = $('question').value.trim();
    if (!q || busy) return;
    busy = true;
    $('askBtn').disabled = true;
    $('resultCard').classList.add('d-none');

    const m = mode();
    const started = Date.now();
    $('status').textContent = 'Asking the AI...';
    const timer = setInterval(() => {
        $('status').textContent = `Asking the AI... ${Math.round((Date.now() - started) / 1000)} s`;
    }, 500);

    const r = await api(m === 'chase' ? '/chase' : '/ask', { method: 'POST', body: JSON.stringify({ question: q }) });

    clearInterval(timer);
    busy = false;
    $('askBtn').disabled = false;
    const secs = ((Date.now() - started) / 1000).toFixed(1);

    if (!r.ok) {
        if (r.data?.generated) {
            $('status').textContent = '';
            showResult('Blocked by the safety check', r.data.generated,
                `<div class="alert alert-warning mb-0">${esc(r.data.error)}</div>`);
            return;
        }
        const busyAi = r.status >= 500;
        $('status').innerHTML = `<span class="text-danger">${esc(busyAi ? 'The AI service is busy. Please try again in a moment.' : (r.data?.error || 'Something went wrong.'))
            }</span>`;
        return;
    }

    if (m === 'chase') { renderChase(r.data, secs); return; }

    const rows = r.data.rows || [];
    $('status').textContent = `Answered in ${secs} s`;
    showResult(`${rows.length} result${rows.length === 1 ? '' : 's'}`, r.data.sql, table(rows));
}

// Example question buttons

$('examples').addEventListener('click', ev => {
    if (ev.target.tagName !== 'BUTTON') return;
    $('question').value = ev.target.textContent;
    ask();
});

$('askBtn').addEventListener('click', ask);
$('question').addEventListener('keydown', ev => { if (ev.key === 'Enter') ask(); });
$('question').focus();

function renderChase(d, secs) {
    const created = d.created || [], skipped = d.skipped || [];
    $('status').textContent = `Done in ${secs} s`;
    let html = created.length
        ? `<div class="alert alert-success">Created ${created.length} proposal${created.length === 1 ? '' : 's'}. Nothing has been sent. A manager must approve each one below.</div>`
        : '<div class="alert alert-info">No new proposals were created.</div>';
    if (created.length) html += table(created.map(c => ({
        approval: c.approval_id, action: c.action, invoice: c.invoice,
        client: c.client, amount: c.amount, days_overdue: c.days_overdue
    })));
    if (skipped.length) html += `<h3 class="h6 mt-3">Skipped (${skipped.length})</h3>
    <ul class="small text-muted mb-0">${skipped.map(s => `<li>${esc(s.invoice || 'Row')}: ${esc(s.reason)}</li>`).join('')}</ul>`;
    showResult(`${created.length} proposed`, d.sql, html);
    refreshAll();
}

const ACTION_LABEL = { SendReminder: 'Send reminder', SendInvoice: 'Send invoice' };

async function loadApprovals() {
    const r = await api('/approvals?status=Pending');
    const list = $('approvalList');
    if (!r.ok) { list.innerHTML = '<div class="text-danger small">Could not load approvals.</div>'; return; }
    const items = r.data || [];
    const isMgr = currentRole() === 'manager';
    $('pendingCount').textContent = items.length;
    $('roleNote').textContent = isMgr
        ? 'Viewing as a manager: you can approve or reject.'
        : 'Switch to Manager (top right) to approve or reject.';
    if (!items.length) {
        list.innerHTML = '<p class="text-muted mb-0">Nothing is waiting. Use Chase mode to create proposals.</p>';
        return;
    }
    list.innerHTML = items.map(a => {
        let note = '';
        try { note = JSON.parse(a.payload).note; } catch { /* no note */ }
        return `<div class="border rounded p-3 mb-2" data-id="${esc(a.id)}">
      <div class="d-flex justify-content-between flex-wrap gap-2">
        <div><strong>${esc(a.client_name)}</strong> &middot; ${esc(a.invoice_number)} &middot; ${money(a.amount)}
          <span class="badge text-bg-info ms-2">${esc(ACTION_LABEL[a.action_type] || a.action_type)}</span></div>
        <div class="text-nowrap">
          <button class="btn btn-success btn-sm" data-act="approve" ${isMgr ? '' : 'disabled'}>Approve</button>
          <button class="btn btn-outline-danger btn-sm" data-act="reject" ${isMgr ? '' : 'disabled'}>Reject</button>
        </div>
      </div>
      <div class="small text-muted mt-2">Message to client: ${esc(note)}</div>
    </div>`;
    }).join('');
}

$('approvalList').addEventListener('click', async ev => {
    const btn = ev.target.closest('button[data-act]');
    if (!btn) return;
    const card = btn.closest('[data-id]');
    card.querySelectorAll('button').forEach(b => b.disabled = true);
    const approve = btn.dataset.act === 'approve';

    const r = await api(`/approvals/${card.dataset.id}/${btn.dataset.act}`,
        { method: 'POST', body: JSON.stringify({ decidedBy: 'manager' }) });

    const box = $('approvalMsg');
    if (r.ok && approve) {
        const url = r.data.pay_url && r.data.pay_url.startsWith('https://') ? r.data.pay_url : null;
        const link = url ? ` <a href="${esc(url)}" target="_blank" rel="noopener">Open the pay link</a> (private window, sandbox buyer login).` : '';
        box.innerHTML = `<div class="alert alert-success">Done: ${esc(String(r.data.action).replace(/_/g, ' '))} for ${esc(r.data.invoice)}.${link}</div>`;
    } else if (r.ok) {
        box.innerHTML = '<div class="alert alert-secondary">Rejected. Nothing was sent.</div>';
    } else {
        box.innerHTML = `<div class="alert alert-danger">${esc(r.data?.error || 'Something went wrong.')} ${esc(String(r.data?.detail || '').slice(0, 200))}</div>`;
    }
    refreshAll();
});

document.querySelectorAll('input[name="mode"]').forEach(el => el.addEventListener('change', () => {
    $('question').placeholder = mode() === 'chase'
        ? 'e.g. Chase the 3 most overdue invoices' : 'e.g. Who owes me the most money?';
    renderExamples();
}));
$('role').addEventListener('change', loadApprovals);
$('reloadApprovals').addEventListener('click', loadApprovals);

let allInvoices = [];
const isOpen = i => i.status === 'Draft' || i.status === 'Sent';

async function loadInvoices() {
    const r = await api('/invoices');
    if (!r.ok) { $('invoiceTable').innerHTML = '<div class="text-danger small">Could not load invoices.</div>'; return; }
    allInvoices = r.data || [];
    renderInvoices();
}

function renderInvoices() {
    const open = allInvoices.filter(isOpen);
    const late = open.filter(i => i.days_overdue > 0);
    const paid = allInvoices.filter(i => i.status === 'Paid');
    const sum = list => list.reduce((s, i) => s + Number(i.amount), 0);
    const tile = (title, list, cls) => `<div class="col-12 col-md-4"><div class="border rounded p-3">
      <div class="small text-muted">${title}</div>
      <div class="fs-4 fw-semibold text-${cls}">${money(sum(list))}</div>
      <div class="small text-muted">${list.length} invoice${list.length === 1 ? '' : 's'}</div></div></div>`;
    $('tiles').innerHTML = tile('Open', open, 'primary') + tile('Overdue', late, 'danger') + tile('Paid', paid, 'success');

    const f = $('invFilter').value;
    const rows = allInvoices.filter(i => f === 'all' || (f === 'paid' ? i.status === 'Paid' : isOpen(i)));
    $('invoiceTable').innerHTML = table(rows.map(i => ({
        invoice_number: i.invoice_number, client_name: i.client_name, amount: i.amount,
        due_date: i.due_date, days_overdue: isOpen(i) ? i.days_overdue : null,
        status: i.status, last_reminder_date: i.last_reminder_date
    })));
}

$('invFilter').addEventListener('change', renderInvoices);

$('refreshStatus').addEventListener('click', async () => {
    const btn = $('refreshStatus');
    btn.disabled = true; btn.textContent = 'Checking PayPal...';
    const r = await api('/invoices/refresh-status', { method: 'POST' });
    btn.disabled = false; btn.textContent = 'Refresh status from PayPal';
    $('refreshMsg').innerHTML = r.ok
        ? `<span class="text-success">Checked ${r.data.inspected} invoice(s) with PayPal: ${r.data.paid} newly paid${r.data.errors ? `, ${r.data.errors} could not be checked` : ''}.</span>`
        : '<span class="text-danger">Could not refresh. Please try again.</span>';
    refreshAll();
});

function describe(a) {
    let d = {};
    try { d = JSON.parse(a.details || '{}'); } catch { /* plain text */ }
    const who = a.actor === 'ai' ? 'AI' : a.actor === 'system' ? 'System'
        : a.actor.charAt(0).toUpperCase() + a.actor.slice(1);
    switch (a.action) {
        case 'chase_requested': return `AI chase request "${d.question || ''}": ${d.created ?? 0} proposed, ${d.skipped ?? 0} skipped`;
        case 'proposal_created': return `AI proposed "${ACTION_LABEL[d.action] || d.action}" for ${d.invoice}`;
        case 'approved': return `${who} approved request #${d.approval_id}`;
        case 'rejected': return `${who} rejected request #${d.approval_id}${d.reason ? ' (' + d.reason + ')' : ''}`;
        case 'invoice_sent': return `PayPal invoice sent for ${d.invoice}`;
        case 'reminder_sent': return `Reminder sent for ${d.invoice}`;
        case 'invoice_issued_as_reminder': return `Invoice issued and sent for ${d.invoice} (first time in PayPal)`;
        case 'action_failed': return `PayPal action failed for request #${d.approval_id}`;
        case 'payment_received': return `Payment received for ${d.invoice}`;
        case 'invoice_cancelled': return `Invoice ${d.invoice} was cancelled`;
        default: return `${who}: ${String(a.action).replace(/_/g, ' ')}`;
    }
}
const ACTOR_CLASS = { ai: 'primary', system: 'secondary' };

async function loadAudit() {
    const r = await api('/audit?limit=30');
    if (!r.ok) { $('timeline').innerHTML = '<div class="text-danger small">Could not load the timeline.</div>'; return; }
    const items = r.data || [];
    $('timeline').innerHTML = items.length ? items.map(a => `
    <div class="d-flex gap-2 py-1 border-bottom">
      <span class="badge text-bg-${ACTOR_CLASS[a.actor] || 'success'} align-self-start mt-1">${esc(a.actor)}</span>
      <div class="flex-grow-1">${esc(describe(a))}</div>
      <div class="small text-muted text-nowrap">${esc(new Date(a.ts).toLocaleString())}</div>
    </div>`).join('') : '<p class="text-muted mb-0">No activity yet.</p>';
}

function refreshAll() { return Promise.all([loadApprovals(), loadInvoices(), loadAudit()]); }

renderExamples();
refreshAll();