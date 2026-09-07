(() => {
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
