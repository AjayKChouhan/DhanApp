import { Routes } from '@angular/router';
import { LoginComponent } from './pages/login/login.component';
import { PositionsComponent } from './pages/positions/positions.component';
import { OrdersComponent } from './pages/orders/orders.component';
import { TradeComponent } from './pages/trade/trade.component';
import { IpSetupComponent } from './pages/ip-setup/ip-setup.component';
import { CopyTradingComponent } from './pages/copy-trading/copy-trading.component';
import { ScannerComponent } from './pages/scanner/scanner.component';

export const routes: Routes = [
  { path: '', redirectTo: 'login', pathMatch: 'full' },
  { path: 'login', component: LoginComponent },
  { path: 'positions', component: PositionsComponent },
  { path: 'orders', component: OrdersComponent },
  { path: 'trade', component: TradeComponent },
  { path: 'scanner', component: ScannerComponent },
  { path: 'copy-trading', component: CopyTradingComponent },
  { path: 'ip-setup', component: IpSetupComponent },
  { path: '**', redirectTo: 'login' }
];
