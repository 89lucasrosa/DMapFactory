/* ==========================================================================
   DMapFactory Help System - Client-side Search and Navigation (Offline)
   ========================================================================== */

document.addEventListener("DOMContentLoaded", function () {
  initSearch();
  initLanguageSwitch();
  highlightActiveNav();
});

// Search functionality
function initSearch() {
  const searchInput = document.querySelector(".search-input");
  if (!searchInput) return;

  const currentPath = window.location.pathname.toLowerCase();
  const isPt = currentPath.includes("/pt-br/") || !currentPath.includes("/en-us/");

  // Pre-indexed search data for instant offline lookup
  const searchIndexPt = [
    { title: "Visão Geral", file: "index.html", keywords: "introdução arquitetura buffer circular opc ua sqlite csv visão geral conceito requisitos" },
    { title: "Conexões e Navegador OPC UA", file: "conexao-opc.html", keywords: "conexão endpoint servidor browse árvore de nós tags variáveis ctrl seleção múltipla autenticação usuário senha segurança" },
    { title: "Gerenciador de Certificados OPC UA", file: "certificados.html", keywords: "segurança pki certificado servidor cliente gerar exportar importar autoassinado chave pública privada confiável der pfx" },
    { title: "Modo Planilha (Wide Format)", file: "planilha.html", keywords: "planilha colunas vertical títulos altura auto-ajuste booleano cores verde cinza tabela timestamp contorno azul celeste" },
    { title: "Modo Gráfico de Tendências e Penas", file: "grafico.html", keywords: "gráfico plot tendência scottplot penas painel de penas degrau step linear mira crosshair cursores a b zoom pan exclusão" },
    { title: "Marcador de Amostras e Sessões", file: "marcador.html", keywords: "marcador anotação evento linha do tempo auditoria parada flag snapshot gravação banco dados exportar" },
    { title: "Atalhos de Teclado e Diagnósticos", file: "atalhos.html", keywords: "atalhos teclas f1 f5 ctrl delete zoom teclado status logs eventos buffer memória conexão diagnóstico" }
  ];

  const searchIndexEn = [
    { title: "Overview", file: "index.html", keywords: "overview introduction architecture circular buffer opc ua sqlite csv concepts requirements" },
    { title: "OPC UA Connections & Tag Browser", file: "opc-connection.html", keywords: "connection endpoint server browse node tree tags variables ctrl multi-select authentication security" },
    { title: "OPC UA Certificate Manager", file: "certificates.html", keywords: "security pki certificate server client generate export import self-signed public private key trusted der pfx" },
    { title: "Spreadsheet Mode (Wide Format)", file: "spreadsheet.html", keywords: "spreadsheet grid columns vertical headers height auto-fit boolean color green gray table timestamp sky blue border" },
    { title: "Trend Chart & Pen Panel", file: "chart.html", keywords: "chart plot trend scottplot pens pen panel step linear crosshair cursors a b zoom pan delete signal" },
    { title: "Sample Marker & Sessions", file: "marker.html", keywords: "marker annotation event timeline audit stop flag snapshot recording database export" },
    { title: "Keyboard Shortcuts & Diagnostics", file: "shortcuts.html", keywords: "shortcuts keys f1 f5 ctrl delete zoom keyboard status logs events buffer memory connection diagnostic" }
  ];

  const activeIndex = isPt ? searchIndexPt : searchIndexEn;

  // Create search results dropdown
  const searchBox = document.querySelector(".search-box");
  const resultsContainer = document.createElement("div");
  resultsContainer.className = "search-results-dropdown";
  resultsContainer.style.display = "none";
  resultsContainer.style.position = "absolute";
  resultsContainer.style.top = "100%";
  resultsContainer.style.left = "0";
  resultsContainer.style.right = "0";
  resultsContainer.style.backgroundColor = "#1E293B";
  resultsContainer.style.border = "1px solid #334155";
  resultsContainer.style.borderRadius = "6px";
  resultsContainer.style.boxShadow = "0 6px 16px rgba(0,0,0,0.3)";
  resultsContainer.style.marginTop = "6px";
  resultsContainer.style.zIndex = "1000";
  resultsContainer.style.maxHeight = "280px";
  resultsContainer.style.overflowY = "auto";
  searchBox.appendChild(resultsContainer);

  searchInput.addEventListener("input", function (e) {
    const term = e.target.value.trim().toLowerCase();
    if (term.length < 2) {
      resultsContainer.style.display = "none";
      resultsContainer.innerHTML = "";
      return;
    }

    const matches = activeIndex.filter(item => 
      item.title.toLowerCase().includes(term) || item.keywords.includes(term)
    );

    if (matches.length === 0) {
      resultsContainer.innerHTML = `<div style="padding: 10px 14px; font-size: 12px; color: #94A3B8;">${isPt ? "Nenhum tópico encontrado." : "No topics found."}</div>`;
      resultsContainer.style.display = "block";
      return;
    }

    resultsContainer.innerHTML = matches.map(m => `
      <a href="${m.file}" style="display: block; padding: 10px 14px; color: #F8FAFC; text-decoration: none; border-bottom: 1px solid #334155; font-size: 13px;">
        <strong style="color: #00B4F0;">${m.title}</strong>
      </a>
    `).join("");
    resultsContainer.style.display = "block";
  });

  document.addEventListener("click", function (e) {
    if (!searchBox.contains(e.target)) {
      resultsContainer.style.display = "none";
    }
  });
}

function initLanguageSwitch() {
  const langBtns = document.querySelectorAll(".lang-btn");
  const currentPage = window.location.pathname.split("/").pop() || "index.html";

  // Map equivalent page names between languages
  const pageMapPtToEn = {
    "index.html": "index.html",
    "conexao-opc.html": "opc-connection.html",
    "certificados.html": "certificates.html",
    "planilha.html": "spreadsheet.html",
    "grafico.html": "chart.html",
    "marcador.html": "marker.html",
    "atalhos.html": "shortcuts.html"
  };

  const pageMapEnToPt = {
    "index.html": "index.html",
    "opc-connection.html": "conexao-opc.html",
    "certificates.html": "certificados.html",
    "spreadsheet.html": "planilha.html",
    "chart.html": "grafico.html",
    "marker.html": "marcador.html",
    "shortcuts.html": "atalhos.html"
  };

  langBtns.forEach(btn => {
    btn.addEventListener("click", function (e) {
      const targetLang = this.getAttribute("data-lang");
      const currentPath = window.location.pathname.toLowerCase();
      const isCurrentlyPt = currentPath.includes("/pt-br/");

      if (targetLang === "pt-BR" && !isCurrentlyPt) {
        e.preventDefault();
        const targetPage = pageMapEnToPt[currentPage] || "index.html";
        window.location.href = `../pt-BR/${targetPage}`;
      } else if (targetLang === "en-US" && isCurrentlyPt) {
        e.preventDefault();
        const targetPage = pageMapPtToEn[currentPage] || "index.html";
        window.location.href = `../en-US/${targetPage}`;
      }
    });
  });
}

function highlightActiveNav() {
  const currentPage = window.location.pathname.split("/").pop() || "index.html";
  const navLinks = document.querySelectorAll(".nav-link");

  navLinks.forEach(link => {
    const href = link.getAttribute("href");
    if (href === currentPage || (currentPage === "" && href === "index.html")) {
      link.classList.add("active");
    } else {
      link.classList.remove("active");
    }
  });
}
