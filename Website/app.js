document.getElementById("copy-url")?.addEventListener("click", async () => {
  const field = document.getElementById("listing-url");
  if (field) await navigator.clipboard.writeText(field.value);
});
