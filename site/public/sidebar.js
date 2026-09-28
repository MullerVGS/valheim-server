// Barra lateral de navegacao, igual em todas as paginas. Para pôr uma pagina ou ferramenta nova, e so
// acrescentar um item em NAV (href sem a barra do comeco, por causa do <base href="/">).
// Tela larga: trilho de icones que abre com os nomes e fica como a pessoa deixou (localStorage).
// Tela media: o trilho abre por cima do conteudo. Celular: gaveta que sai da esquerda pelo botao de menu.

const PINNED_KEY = 'valheim.sidebar';
const WIDE = window.matchMedia('(min-width: 1001px)');

const ICONS = {
  map: '<path d="M9 4 3 6.5v13.5l6-2.5 6 2.5 6-2.5V4l-6 2.5z"/><path d="M9 4v13.5M15 6.5V20"/>',
  sign: '<rect x="3" y="4" width="18" height="10" rx="1.5"/><path d="M7 8h10M7 11h6M12 14v7M9 21h6"/>',
  menu: '<path d="M4 7h16M4 12h16M4 17h16"/>',
  collapse: '<path d="M15 6l-6 6 6 6"/>',
};
const icon = (name) => `<svg viewBox="0 0 24 24" aria-hidden="true">${ICONS[name]}</svg>`;

// match: caminhos que acendem o item (prefixo). soon: aparece, mas ainda sem pagina.
const NAV = [
  { title: 'Mundo', items: [
    { label: 'Mapa', href: './', icon: 'map', match: ['/', '/base/', '/jogador/'] },
  ] },
  { title: 'Ferramentas', items: [
    { label: 'Placas', href: 'placas', icon: 'sign', match: ['/placas'], soon: true },
  ] },
];

const here = location.pathname;
const isActive = (item) => item.match.some((m) => (m === '/' ? here === '/' || here === '/index.html' : here.startsWith(m)));

function navItem(item) {
  const active = isActive(item);
  const tag = item.soon ? 'span' : 'a';
  const attrs = item.soon
    ? 'aria-disabled="true"'
    : `href="${item.href}"${active ? ' aria-current="page"' : ''}`;
  return `<li><${tag} class="side-link${item.soon ? ' soon' : ''}" ${attrs} data-tip="${item.label}">`
    + `<span class="side-icon">${icon(item.icon)}</span>`
    + `<span class="side-label">${item.label}</span>`
    + (item.soon ? '<span class="side-badge">em breve</span>' : '')
    + `</${tag}></li>`;
}

const sidebar = document.createElement('aside');
sidebar.className = 'sidebar';
sidebar.id = 'sidebar';
sidebar.setAttribute('aria-label', 'Navegação do site');
sidebar.innerHTML = `
  <a class="side-brand" href="./" aria-label="Valheim, ir para o mapa">
    <span class="side-mark" aria-hidden="true">J</span>
    <span class="side-name">Valheim</span>
  </a>
  <nav class="side-nav">
    ${NAV.map((g) => `
      <div class="side-group">
        <h2 class="side-title">${g.title}</h2>
        <ul>${g.items.map(navItem).join('')}</ul>
      </div>`).join('')}
  </nav>
  <button class="side-collapse" type="button" aria-controls="sidebar">
    <span class="side-icon">${icon('collapse')}</span>
    <span class="side-label">Recolher</span>
  </button>`;

const scrim = document.createElement('div');
scrim.className = 'side-scrim';

const opener = document.createElement('button');
opener.className = 'side-opener';
opener.type = 'button';
opener.setAttribute('aria-controls', 'sidebar');
opener.setAttribute('aria-label', 'Abrir o menu');
opener.innerHTML = icon('menu');

document.body.prepend(sidebar, scrim, opener);

const root = document.documentElement;
const toggle = sidebar.querySelector('.side-collapse');

const expanded = () => (WIDE.matches ? root.classList.contains('rail-pinned') : root.classList.contains('rail-peek'));

const PHONE = window.matchMedia('(max-width: 720px)');
const toggleLabel = toggle.querySelector('.side-label');

function sync() {
  const open = expanded();
  const text = PHONE.matches ? 'Fechar' : open ? 'Recolher' : 'Expandir';
  toggle.setAttribute('aria-expanded', String(open));
  toggle.setAttribute('aria-label', `${text} o menu`);
  toggle.title = text;
  toggleLabel.textContent = text;
  opener.setAttribute('aria-expanded', String(open));
}

function setPeek(on, focus = false) {
  root.classList.toggle('rail-peek', on);
  sync();
  if (on && focus) sidebar.querySelector('a[href]')?.focus({ preventScroll: true });
}

toggle.addEventListener('click', () => {
  if (!WIDE.matches) return setPeek(!root.classList.contains('rail-peek'));
  const pinned = root.classList.toggle('rail-pinned');
  try { localStorage.setItem(PINNED_KEY, pinned ? 'open' : 'closed'); } catch {}
  sync();
});
opener.addEventListener('click', () => setPeek(true, true));
scrim.addEventListener('click', () => setPeek(false));
document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape' && root.classList.contains('rail-peek')) { setPeek(false); opener.focus({ preventScroll: true }); }
});
// Tela media: clicar fora (no mapa) fecha o trilho aberto por cima.
document.addEventListener('pointerdown', (e) => {
  if (root.classList.contains('rail-peek') && !sidebar.contains(e.target) && !opener.contains(e.target)) setPeek(false);
});

// Celular: arrastar a gaveta para a esquerda fecha.
let drag = null;
sidebar.addEventListener('touchstart', (e) => {
  if (!PHONE.matches || !root.classList.contains('rail-peek') || e.touches.length !== 1) return;
  drag = { x: e.touches[0].clientX, y: e.touches[0].clientY, dx: 0, horizontal: null };
}, { passive: true });
sidebar.addEventListener('touchmove', (e) => {
  if (!drag) return;
  const dx = e.touches[0].clientX - drag.x;
  const dy = e.touches[0].clientY - drag.y;
  if (drag.horizontal === null && Math.hypot(dx, dy) > 8) drag.horizontal = Math.abs(dx) > Math.abs(dy);
  if (!drag.horizontal) return;
  drag.dx = Math.min(0, dx);
  sidebar.classList.add('dragging');
  sidebar.style.transform = `translateX(${drag.dx}px)`;
}, { passive: true });
const endDrag = () => {
  if (!drag) return;
  const close = drag.horizontal && drag.dx < -sidebar.offsetWidth * 0.3;
  sidebar.classList.remove('dragging');
  sidebar.style.transform = '';
  drag = null;
  if (close) setPeek(false);
};
sidebar.addEventListener('touchend', endDrag);
sidebar.addEventListener('touchcancel', endDrag);
for (const mq of [WIDE, PHONE]) mq.addEventListener('change', () => { root.classList.remove('rail-peek'); sync(); });
sync();
// Sem transicao no primeiro quadro, para a barra nao "abrir" a cada pagina.
requestAnimationFrame(() => requestAnimationFrame(() => root.classList.add('rail-ready')));
