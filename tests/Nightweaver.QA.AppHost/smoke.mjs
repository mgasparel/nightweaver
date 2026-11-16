import { chromium } from 'playwright';

const url = process.env.NIGHTWEAVER_QA_URL;
if (!url) throw new Error('Set NIGHTWEAVER_QA_URL to the web endpoint from aspire describe.');
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
  await page.goto(url);
  await page.getByRole('navigation', { name: 'Project navigation' }).getByRole('link', { name: 'Beads', exact: true }).click();
  await page.getByRole('combobox', { name: 'View', exact: true }).selectOption('active');
  await page.getByRole('button', { name: 'Search project', exact: true }).click();
  await page.getByText('Active QA task', { exact: true }).waitFor();
  if (errors.length) throw new Error(errors.join('\n'));
  console.log(`Playwright reached ${url}: ${await page.title()}; seeded issue is visible.`);
} finally {
  await browser.close();
}
