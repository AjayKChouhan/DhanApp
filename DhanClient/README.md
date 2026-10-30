# Dhanclient

This project was generated using [Angular CLI](https://github.com/angular/angular-cli) version 19.2.27.

## Development server

To start a local development server, run:

```bash
ng serve
```

Once the server is running, open your browser and navigate to `http://localhost:4200/`. The application will automatically reload whenever you modify any of the source files.

## Code scaffolding

Angular CLI includes powerful code scaffolding tools. To generate a new component, run:

```bash
ng generate component component-name
```

For a complete list of available schematics (such as `components`, `directives`, or `pipes`), run:

```bash
ng generate --help
```

## Building

To build the project run:

```bash
ng build
```

This will compile your project and store the build artifacts in the `dist/` directory. By default, the production build optimizes your application for performance and speed.

## Running unit tests

To execute unit tests with the [Karma](https://karma-runner.github.io) test runner, use the following command:

```bash
ng test
```

## 5-Minute F&O Price-Move Scanner

The API scans NSE EQ cash-equity scrips that are underlyings of NSE F&O contracts, sourced from Dhan's instrument
master, using Dhan's five-minute equity candles. Derivative contracts and BSE listings are excluded. It compares the
latest fully closed candle's close with the immediately previous fully
closed candle's close and reports LONG moves of at least 2% or SHORT moves of at least 2%. It requests a short recent
candle window and keeps scan results in memory; this scanner does not read from or write to a database. The worker
scans after each five-minute boundary during market hours, and the Scanner page refreshes results every 30 seconds.
No orders are placed.

## Copy Trading

The copy setup supports one Dhan master with Dhan, Angel One, and Upstox futures children. The API listens to Dhan's
[Live Order Update WebSocket](https://dhanhq.co/docs/v2/order-update/) and resolves the matching underlying and expiry
from each child broker's instrument master. Missing or ambiguous contracts are rejected. Supported pending-order changes
and cancellations are routed through the relevant child broker; the API polls Angel One and Upstox order books for those
child statuses and listens to Dhan order updates for Dhan child statuses. The API must remain running.
The master order's lot count is derived from its Dhan contract quantity and lot size, then converted using the child
broker's lot size; equal lot sizes are not required.

Add Angel One accounts to `DhanAPI/App_Data/DhanTrading/dhan-accounts.local.json` using the following property names
(use your own values locally; never commit this ignored plaintext file):

```json
{
  "accountName": "Angel One account",
  "broker": "ANGELONE",
  "clientCode": "YOUR_CLIENT_CODE",
  "password": "YOUR_PASSWORD_OR_PIN",
  "apiKey": "YOUR_API_KEY",
  "totpSecret": "YOUR_BASE32_TOTP_SECRET"
}
```

The file is an array, so add the object alongside the existing Dhan account objects. Dhan entries continue to use
`dhanClientId`, `pin`, and `totpSecret`. Click **Login All Accounts** to import credentials into the protected local
account store and create an Angel One session. The source JSON remains plaintext on disk and must remain private.

For an Upstox child, add an entry with `broker`, `accountName`, `clientCode` (the Upstox user ID), `apiKey`, and
`apiSecret`. Add an Upstox developer application and register the exact redirect URI
`http://localhost:5050/api/dhantrading/upstox/callback` for local development. For another host, set
`DhanTrading:UpstoxRedirectUri` to that host's HTTPS callback URL and register the same URL with Upstox. Save the account
in the Login page, then click **Connect Upstox** and complete authorization in the Upstox tab. Upstox issues daily OAuth
access tokens, so reconnect after expiry. API keys, secrets, and access tokens are encrypted in the protected account/token
stores; never commit them or the local plaintext accounts file.

Cross-broker copying is not an execution guarantee. Orders can be rejected or filled at different prices, and a delayed
network response can still leave order submission uncertain.

Copy settings and activity remain in the existing encrypted local JSON data store under
`DhanAPI/App_Data/DhanTrading`.

## Running end-to-end tests

For end-to-end (e2e) testing, run:

```bash
ng e2e
```

Angular CLI does not come with an end-to-end testing framework by default. You can choose one that suits your needs.

## Additional Resources

For more information on using the Angular CLI, including detailed command references, visit the [Angular CLI Overview and Command Reference](https://angular.dev/tools/cli) page.
