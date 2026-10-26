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

Open **Copy Trading** to choose a saved master account, enable child accounts, and monitor per-account order results.
The API listens to Dhan's [Live Order Update WebSocket](https://dhanhq.co/docs/v2/order-update/), which documents updates
for an individual account's orders irrespective of the platform used to place them. The API must remain running for live
copying. Supported pending-order modifications (price, quantity, order type, and validity) and cancellations are mirrored
to the child orders using Dhan's order APIs. They can fail if a child order is no longer pending or Dhan rejects the change.
Bracket/cover, after-market, and disclosed-quantity orders are reported as unsupported.

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
