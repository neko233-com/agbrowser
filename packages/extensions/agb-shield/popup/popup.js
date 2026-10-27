const toggle = document.getElementById('toggle');
const totalEl = document.getElementById('total');
const hostsEl = document.getElementById('hosts');

async function refresh() {
  const { shieldStats = {}, shieldDisabled = false } = await chrome.storage.local.get([
    'shieldStats', 'shieldDisabled',
  ]);
  totalEl.textContent = shieldStats.total ?? 0;
  toggle.setAttribute('aria-checked', String(!shieldDisabled));

  const entries = Object.entries(shieldStats.byHost ?? {})
    .sort((a, b) => b[1] - a[1])
    .slice(0, 5);
  hostsEl.innerHTML = entries.length
    ? '主要来源：<br>' + entries.map(([h, c]) => `${h} (${c})`).join('<br>')
    : '尚未拦截到追踪请求';
}

toggle.addEventListener('click', async () => {
  const { shieldDisabled = false } = await chrome.storage.local.get('shieldDisabled');
  await chrome.storage.local.set({ shieldDisabled: !shieldDisabled });
  await refresh();
});

chrome.storage.onChanged.addListener(refresh);
refresh();
