(function(){
  const DEFS = [
    {cls:'g-news', code:'0', label:'ニュース'},
    {cls:'g-sports', code:'1', label:'スポーツ'},
    {cls:'g-info', code:'2', label:'情報'},
    {cls:'g-drama', code:'3', label:'ドラマ'},
    {cls:'g-music', code:'4', label:'音楽'},
    {cls:'g-variety', code:'5', label:'バラエティ'},
    {cls:'g-movie', code:'6', label:'映画'},
    {cls:'g-anime', code:'7', label:'アニメ'},
    {cls:'g-docu', code:'8', label:'ドキュメント'},
    {cls:'g-other', code:null, label:'その他'}
  ];
  const CODE_TO_CLASS = {
    '0':'g-news','1':'g-sports','2':'g-info','3':'g-drama','4':'g-music',
    '5':'g-variety','6':'g-movie','7':'g-anime','8':'g-docu'
  };
  const VAR_BY_CLASS = {
    'g-news':'--genre-news',
    'g-sports':'--genre-sports',
    'g-info':'--genre-info',
    'g-drama':'--genre-drama',
    'g-music':'--genre-music',
    'g-variety':'--genre-variety',
    'g-movie':'--genre-movie',
    'g-anime':'--genre-anime',
    'g-docu':'--genre-docu',
    'g-other':'--genre-other'
  };

  // Light keeps the legacy pastel palette. Dark is a readability-first EPG palette owned here.
  const LIGHT_COLORS = Object.freeze({
    'g-news':'#d3ffcb','g-sports':'#ffcbee','g-info':'#b8f0ac','g-drama':'#ffbbbb','g-music':'#b4f2ff',
    'g-variety':'#faffb4','g-movie':'#cbfcf4','g-anime':'#dcdcfe','g-docu':'#f0f0f0','g-other':'#f0f0f0'
  });
  const DARK_COLORS = Object.freeze({
    'g-news':'#1f5a45','g-sports':'#245c7a','g-info':'#2d6f61','g-drama':'#6b3341','g-music':'#286b78',
    'g-variety':'#6b5a24','g-movie':'#563a73','g-anime':'#394f95','g-docu':'#3f5366','g-other':'#4a5058'
  });
  const LEGACY_DARK_DEFAULTS = Object.freeze([
    Object.freeze({'g-news':'#2f6b3f','g-sports':'#7a3a63','g-info':'#2f6540','g-drama':'#7a3c3c','g-music':'#2f6878','g-variety':'#756d2f','g-movie':'#2f6d66','g-anime':'#55579a','g-docu':'#56616d','g-other':'#4b5563'}),
    Object.freeze({'g-news':'#244c3a','g-sports':'#204b63','g-info':'#2f5e46','g-drama':'#5a2a32','g-music':'#284e5a','g-variety':'#5a4a22','g-movie':'#4d365e','g-anime':'#343f73','g-docu':'#2f465a','g-other':'#3f4248'})
  ]);
  const LIGHT_SAMPLE_COLORS = Object.freeze([
    '#d3ffcb','#ffcbee','#b8f0ac','#ffbbbb',
    '#b4f2ff','#faffb4','#cbfcf4','#dcdcfe',
    '#f0f0f0','#fff0c2','#ffd8b4','#c8e0ff',
    '#e6d0ff','#c9ffd9','#ffe1ec','#e8e8e8'
  ]);
  const DARK_SAMPLE_COLORS = Object.freeze([
    '#1f5a45','#245c7a','#2d6f61','#6b3341',
    '#286b78','#6b5a24','#563a73','#394f95',
    '#3f5366','#7a5f2a','#704456','#2f6670',
    '#664985','#35705e','#7a3f4e','#4a5058'
  ]);
  let currentTheme = 'light';
  let currentPalettes = {
    light: Object.assign({}, LIGHT_COLORS),
    dark: Object.assign({}, DARK_COLORS)
  };

  function normalizeHexColor(value, fallback){
    const raw = (value == null ? '' : String(value)).trim();
    const m = raw.match(/^#?([0-9a-fA-F]{6})$/);
    return m ? ('#' + m[1].toLowerCase()) : fallback;
  }
  function normalizePalette(map, fallback){
    const src = map || {};
    const out = {};
    Object.keys(VAR_BY_CLASS).forEach(cls => {
      out[cls] = normalizeHexColor(src[cls], fallback[cls] || LIGHT_COLORS[cls] || '#f0f0f0');
    });
    return out;
  }
  function normalizeThemeName(theme){
    const v = String(theme || '').trim().toLowerCase();
    if(v === 'dark') return 'dark';
    if(v === 'light') return 'light';
    const attr = (document.documentElement.getAttribute('data-theme') || document.body.getAttribute('data-theme') || '').toLowerCase();
    if(attr === 'dark' || document.body.classList.contains('theme-dark')) return 'dark';
    return 'light';
  }
  function paletteEquals(a, b){
    return Object.keys(VAR_BY_CLASS).every(cls => normalizeHexColor(a && a[cls], '') === normalizeHexColor(b && b[cls], ''));
  }
  function migrateDarkDefaultPalette(map){
    return LEGACY_DARK_DEFAULTS.some(legacy => paletteEquals(map, legacy)) ? Object.assign({}, DARK_COLORS) : map;
  }
  function normalizeThemePalettes(settings){
    const src = settings || {};
    const tp = src.themeGenrePalettes || src.ThemeGenrePalettes || null;
    const lightSrc = (tp && (tp.light || tp.Light)) || src.lightGenreColors || src.LightGenreColors || src.genreColors || src.GenreColors || src.defaultGenreColors || src.DefaultGenreColors || LIGHT_COLORS;
    const darkSrc = (tp && (tp.dark || tp.Dark)) || src.darkGenreColors || src.DarkGenreColors || DARK_COLORS;
    const light = normalizePalette(lightSrc, LIGHT_COLORS);
    const dark = migrateDarkDefaultPalette(normalizePalette(darkSrc, DARK_COLORS));
    return { light, dark };
  }
  function applyPaletteForTheme(theme, palettes){
    currentTheme = normalizeThemeName(theme);
    if(palettes) currentPalettes = normalizeThemePalettes({ themeGenrePalettes: palettes });
    const colors = normalizePalette(currentPalettes[currentTheme], currentTheme === 'dark' ? DARK_COLORS : LIGHT_COLORS);
    Object.keys(VAR_BY_CLASS).forEach(cls => document.documentElement.style.setProperty(VAR_BY_CLASS[cls], colors[cls]));
    window.dispatchEvent(new CustomEvent('tvair:genre-colors-applied', { detail: { theme: currentTheme, colors: colorsSnapshot(), palettes: currentPalettes } }));
    return colors;
  }
  function applyGenreColors(map){
    // Compatibility path: old callers pass a single map.  It now updates the active theme palette only.
    const theme = normalizeThemeName(currentTheme);
    currentPalettes = Object.assign({}, currentPalettes, { [theme]: normalizePalette(map, theme === 'dark' ? DARK_COLORS : LIGHT_COLORS) });
    return applyPaletteForTheme(theme, currentPalettes);
  }
  function applyThemeGenrePalettes(settings, theme){
    currentPalettes = normalizeThemePalettes(settings || {});
    return applyPaletteForTheme(theme || (settings && settings.systemTheme), currentPalettes);
  }
  async function syncGenreColorsFromSettings(){
    try{
      const r = await fetch('/api/settings?ts=' + Date.now(), { cache:'no-store' });
      if(!r || !r.ok) return false;
      const d = await r.json();
      applyThemeGenrePalettes(d, d.effectiveTheme || d.systemTheme || currentTheme);
      return true;
    }catch(_){
      return false;
    }
  }
  function codeToClass(code){
    const raw = code == null ? '' : String(code).trim();
    if(!raw) return 'g-other';
    let key = '';
    const hex = raw.match(/^(?:0x)?([0-9a-fA-F])/);
    if(hex) key = hex[1].toLowerCase();
    if(!key && /^\d+$/.test(raw)) key = String(Math.floor(Number(raw) / (Number(raw) > 15 ? 16 : 1))).charAt(0);
    return CODE_TO_CLASS[key] || 'g-other';
  }
  function labelToClass(label){
    const text = String(label || '').toLowerCase();
    if(/ニュース|報道|news/.test(text)) return 'g-news';
    if(/スポーツ|sports/.test(text)) return 'g-sports';
    if(/情報|ワイド|info/.test(text)) return 'g-info';
    if(/ドラマ|drama/.test(text)) return 'g-drama';
    if(/音楽|music/.test(text)) return 'g-music';
    if(/バラエティ|variety/.test(text)) return 'g-variety';
    if(/映画|movie/.test(text)) return 'g-movie';
    if(/アニメ|特撮|anime/.test(text)) return 'g-anime';
    if(/ドキュメント|ドキュメンタリー|教養|document/.test(text)) return 'g-docu';
    return 'g-other';
  }
  function codesToClass(codes, label){
    const raw = codes == null ? '' : String(codes).trim();
    if(raw){
      const first = raw.split(/[;,\s|/]+/).map(x => x.trim()).find(Boolean);
      const cls = codeToClass(first);
      if(cls && cls !== 'g-other') return cls;
    }
    return labelToClass(label);
  }
  function colorByClass(cls){
    const c = VAR_BY_CLASS[cls] ? getComputedStyle(document.documentElement).getPropertyValue(VAR_BY_CLASS[cls]).trim() : '';
    return normalizeHexColor(c, (currentTheme === 'dark' ? DARK_COLORS : LIGHT_COLORS)[cls] || LIGHT_COLORS['g-other']);
  }
  function colorByCodes(codes){ return colorByClass(codesToClass(codes)); }
  function colorsSnapshot(){
    const out={};
    Object.keys(VAR_BY_CLASS).forEach(k => out[k] = colorByClass(k));
    return out;
  }
  function palettesSnapshot(){
    return {
      light: normalizePalette(currentPalettes.light, LIGHT_COLORS),
      dark: normalizePalette(currentPalettes.dark, DARK_COLORS)
    };
  }

  window.TvAirGenre = Object.freeze({
    DEFS: Object.freeze(DEFS.slice()),
    DEFAULT_COLORS: LIGHT_COLORS,
    LIGHT_COLORS,
    DARK_COLORS,
    LIGHT_SAMPLE_COLORS,
    DARK_SAMPLE_COLORS,
    VAR_BY_CLASS: Object.freeze(Object.assign({}, VAR_BY_CLASS)),
    get COLORS(){ return colorsSnapshot(); },
    get THEME_PALETTES(){ return palettesSnapshot(); },
    get CURRENT_THEME(){ return currentTheme; },
    normalizeHexColor,
    normalizePalette,
    normalizeThemePalettes,
    codeToClass,
    codesToClass,
    labelToClass,
    colorByClass,
    colorByCodes,
    applyGenreColors,
    applyPaletteForTheme,
    applyThemeGenrePalettes,
    syncGenreColorsFromSettings
  });
})();
