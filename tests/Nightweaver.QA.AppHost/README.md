# QA AppHost

Requires .NET 10, Aspire CLI, Git, `bd`, Node.js and npm. From this directory, install the browser tools once:

```powershell
npm ci
npx playwright install chromium
```

Start a fresh isolated stack and discover the dynamic `web` URL:

```powershell
aspire start --isolated --non-interactive
aspire wait web --non-interactive
aspire describe --format Json --non-interactive
```

Use the `url` of the `http` endpoint of the `web` resource from `aspire describe`. As a connectivity check, set `NIGHTWEAVER_QA_URL` to that URL and run `node smoke.mjs`. For actual QA, use Playwright in a script or an available agent Playwright tool to navigate and interact with that URL. Run scripts from this directory so `playwright` resolves from its local installation; capture `pageerror` and `console` error events as in `smoke.mjs`. Close the browser when finished. The `web-browser-logs` dashboard resource captures logs from its own tracked browser session, not arbitrary Playwright sessions.

Always stop the same AppHost in a finally/cleanup step:

```powershell
aspire stop --non-interactive
```

Each run creates a disposable Git repository and Beads tracker with sample issues. UI writes affect that fixture; stop removes it. The fixture is not a copy of the user's tracker. Do not use desktop Computer Use (`cua_repl/js`) to drive browser QA.
