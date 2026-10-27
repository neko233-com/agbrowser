const q = document.getElementById('q');
const hist = document.getElementById('hist');
const bm = document.getElementById('bm');

function render(el, items, emptyText) {
  if (!items.length) {
    el.innerHTML = `<div class="empty">${emptyText}</div>`;
    return;
  }
  el.innerHTML = items
    .map((i) => `<div class="item" data-url="${i.url.replace(/"/g, '&quot;')}">
        <div class="t">${escapeHtml(i.title || i.url)}</div>
        <div class="u">${escapeHtml(i.url)}</div>
      </div>`)
    .join('');
  el.querySelectorAll('.item').forEach((node) => {
    node.addEventListener('click', () => {
      chrome.tabs.create({ url: node.dataset.url, active: true });
      window.close();
    });
  });
}

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}

async function search() {
  const query = q.value.trim();
  if (!query) {
    hist.innerHTML = '<div class="empty">输入关键词开始搜索</div>';
    bm.innerHTML = '';
    return;
  }
  const res = await chrome.runtime.sendMessage({ type: 'agb:search:everything', query });
  if (!res?.ok) {
    hist.innerHTML = `<div class="empty">搜索失败：${res?.error}</div>`;
    return;
  }
  render(hist, res.result.history, '无匹配历史记录');
  render(bm, res.result.bookmarks, '无匹配书签');
}

let timer;
q.addEventListener('input', () => {
  clearTimeout(timer);
  timer = setTimeout(search, 180);
});
q.focus();
search();
