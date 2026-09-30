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

  function readCssBootstrapPalette(){
    const style=getComputedStyle(document.documentElement);
    const result={};
    Object.keys(VAR_BY_CLASS).forEach(cls=>{
      const value=style.getPropertyValue(VAR_BY_CLASS[cls]).trim();
      result[cls]=/^#[0-9a-fA-F]{6}$/.test(value) ? value.toLowerCase() : '#f0f0f0';
    });
    return result;
  }
  // CSS is only a neutral first-paint bootstrap. The server-owned default palettes replace both maps through settings.
  const BOOTSTRAP_COLORS = readCssBootstrapPalette();
  const LIGHT_COLORS = Object.assign({}, BOOTSTRAP_COLORS);
  const DARK_COLORS = Object.assign({}, BOOTSTRAP_COLORS);
  // Preset choices are projected from SettingsDefaults through /api/settings.
  // Browser code owns no light/dark palette literals.
  const LIGHT_SAMPLE_COLORS = [];
  const DARK_SAMPLE_COLORS = [];

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
    const attr = (document.documentElement.getAttribute('data-tvair-effective-theme') || document.body.getAttribute('data-tvair-effective-theme') || document.documentElement.getAttribute('data-theme') || document.body.getAttribute('data-theme') || '').toLowerCase();
    if(attr === 'dark' || document.body.classList.contains('theme-dark')) return 'dark';
    return 'light';
  }
  function normalizeThemePalettes(settings){
    const src = settings || {};
    const defaults = src.defaultThemeGenrePalettes || src.DefaultThemeGenrePalettes || null;
    if(defaults){
      const defaultLight = defaults.light || defaults.Light;
      const defaultDark = defaults.dark || defaults.Dark;
      if(defaultLight) Object.assign(LIGHT_COLORS, normalizePalette(defaultLight, LIGHT_COLORS));
      if(defaultDark) Object.assign(DARK_COLORS, normalizePalette(defaultDark, DARK_COLORS));
    }
    const presets = src.themeGenrePresetColors || src.ThemeGenrePresetColors || null;
    if(presets){
      const lightPresets = presets.light || presets.Light;
      const darkPresets = presets.dark || presets.Dark;
      if(Array.isArray(lightPresets)){ LIGHT_SAMPLE_COLORS.splice(0, LIGHT_SAMPLE_COLORS.length, ...lightPresets.map(x=>normalizeHexColor(x, '')).filter(Boolean)); }
      if(Array.isArray(darkPresets)){ DARK_SAMPLE_COLORS.splice(0, DARK_SAMPLE_COLORS.length, ...darkPresets.map(x=>normalizeHexColor(x, '')).filter(Boolean)); }
    }
    const tp = src.themeGenrePalettes || src.ThemeGenrePalettes || null;
    const lightSrc = (tp && (tp.light || tp.Light)) || LIGHT_COLORS;
    const darkSrc = (tp && (tp.dark || tp.Dark)) || DARK_COLORS;
    return { light: normalizePalette(lightSrc, LIGHT_COLORS), dark: normalizePalette(darkSrc, DARK_COLORS) };
  }
  function applyPaletteForTheme(theme, palettes){
    currentTheme = normalizeThemeName(theme);
    if(palettes) currentPalettes = normalizeThemePalettes({ themeGenrePalettes: palettes });
    const colors = normalizePalette(currentPalettes[currentTheme], currentTheme === 'dark' ? DARK_COLORS : LIGHT_COLORS);
    Object.keys(VAR_BY_CLASS).forEach(cls => {
      const color = colors[cls];
      document.documentElement.style.setProperty(VAR_BY_CLASS[cls], color);
      const key = String(cls || 'g-other').replace(/^g-/, '');
      const contrast = window.TvAIrTheme && typeof window.TvAIrTheme.resolveContrast === 'function'
        ? window.TvAIrTheme.resolveContrast(color)
        : null;
      if(contrast){
        document.documentElement.style.setProperty('--genre-' + key + '-fg-main', contrast.main);
        document.documentElement.style.setProperty('--genre-' + key + '-fg-soft', contrast.soft);
        document.documentElement.style.setProperty('--genre-' + key + '-fg-muted', contrast.muted);
      }
    });
    window.dispatchEvent(new CustomEvent('tvair:genre-colors-applied', { detail: { theme: currentTheme, colors: colorsSnapshot(), palettes: currentPalettes } }));
    return colors;
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

  // Theme State is Host-owned. Genre palette projection is shared here so every
  // consumer (ProgramGuide and reservation-family lists) follows the same effective theme
  // without page-local theme listeners or independent theme inference.
  window.addEventListener('tvair-theme-state-applied', ev => {
    const detail = (ev && ev.detail) || {};
    applyPaletteForTheme(detail.effective || detail.rawEffective || detail.selected || currentTheme, currentPalettes);
  });

  window.TvAirGenre = Object.freeze({
    DEFS: Object.freeze(DEFS.slice()),
    LIGHT_COLORS,
    DARK_COLORS,
    get LIGHT_SAMPLE_COLORS(){ return LIGHT_SAMPLE_COLORS.slice(); },
    get DARK_SAMPLE_COLORS(){ return DARK_SAMPLE_COLORS.slice(); },
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
    applyPaletteForTheme,
    applyThemeGenrePalettes,
    syncGenreColorsFromSettings
  });
})();
