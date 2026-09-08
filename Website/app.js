(() => {
  const guide = document.getElementById("install-guide");
  if (guide && typeof guide.showModal === "function") {
    document.querySelectorAll("[data-install-guide]").forEach(link => {
      link.addEventListener("click", event => {
        if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
        event.preventDefault();
        guide.showModal();
      });
    });
  }

  const button = document.getElementById("copy-url");
  const field = document.getElementById("listing-url");
  const status = document.getElementById("copy-status");
  if (!button || !field || !status) return;

  button.hidden = false;
  field.addEventListener("click", () => field.select());
  button.addEventListener("click", async () => {
    status.textContent = "";
    try {
      await navigator.clipboard.writeText(field.value);
      status.textContent = "コピーしました。リポジトリの追加画面に貼り付けてください。";
    } catch {
      field.focus();
      field.select();
      status.textContent = "URLを選択しました。端末のコピー操作でコピーしてください。";
    }
  });
})();
