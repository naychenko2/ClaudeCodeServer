// Минимальный пульт спайка: запускает сценарий в service worker и пишет статус.

const status = document.getElementById('status');

document.querySelectorAll('button[data-s]').forEach((b) => {
  b.addEventListener('click', () => {
    status.textContent = 'Запуск: ' + b.dataset.s + '…';
    chrome.runtime.sendMessage({ scenario: b.dataset.s }, (res) => {
      if (chrome.runtime.lastError) { status.textContent = 'Ошибка: ' + chrome.runtime.lastError.message; return; }
      status.textContent = res && res.ok ? 'Готово: ' + b.dataset.s : 'Провал: ' + (res && res.error);
    });
  });
});

chrome.runtime.sendMessage({ ping: true }, () => {
  status.textContent = chrome.runtime.lastError ? 'SW не отвечает' : 'SW на связи, сценарии доступны';
});
