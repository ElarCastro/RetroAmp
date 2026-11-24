const { chromium } = require('playwright');
(async () => {
  const b = await chromium.launch(); const pg = await b.newPage();
  await pg.goto('file://' + __dirname + '/manual.html'); await pg.waitForTimeout(800);
  await pg.pdf({ path: __dirname + '/ManualUsuario.pdf', format: 'A4', printBackground: true, displayHeaderFooter: true,
    headerTemplate: '<div></div>',
    footerTemplate: '<div style="width:100%;font:8px Arial;color:#777;padding:0 15mm;display:flex;justify-content:space-between"><span>RetroAmp · Manual de usuario</span><span>Página <span class="pageNumber"></span> de <span class="totalPages"></span></span></div>',
    margin: { top: '16mm', bottom: '18mm', left: '15mm', right: '15mm' } });
  await b.close();
})();
