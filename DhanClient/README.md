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

## Copy Trading

The copy setup supports one Dhan master with Dhan and Angel One futures children. The API listens to Dhan's
[Live Order Update WebSocket](https://dhanhq.co/docs/v2/order-update/) and resolves the matching underlying and expiry
from Angel One's instrument master for Angel One children. Missing or ambiguous contracts are rejected. Supported
pending-order changes and cancellations are routed through the relevant child broker; the API polls Angel One's order
book for Angel One child statuses and listens to Dhan order updates for Dhan child statuses. The API must remain running.
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

Cross-broker copying is not an execution guarantee. Orders can be rejected or filled at different prices, and a delayed
network response can still leave order submission uncertain.

Copy settings and activity are stored in the existing encrypted local JSON data store under
`DhanAPI/App_Data/DhanTrading`; this project currently does not use a SQL database.

## Running end-to-end tests

For end-to-end (e2e) testing, run:

```bash
ng e2e
```

Angular CLI does not come with an end-to-end testing framework by default. You can choose one that suits your needs.

## Additional Resources

For more information on using the Angular CLI, including detailed command references, visit the [Angular CLI Overview and Command Reference](https://angular.dev/tools/cli) page.
