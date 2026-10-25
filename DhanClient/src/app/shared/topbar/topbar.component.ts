import { Component } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { DhanConfigService } from '../../core/dhan-config.service';

// Shared topbar (nav + settings panel) shown on every page.
@Component({
  selector: 'app-topbar',
  imports: [RouterLink, RouterLinkActive, FormsModule],
  templateUrl: './topbar.component.html'
})
export class TopbarComponent {
  panelOpen = false;
  baseUrl: string;
  apiKey: string;

  constructor(private readonly config: DhanConfigService) {
    this.baseUrl = this.config.getBaseUrl();
    this.apiKey = this.config.getApiKey();
  }

  togglePanel(): void {
    this.panelOpen = !this.panelOpen;
  }

  save(): void {
    this.config.setBaseUrl(this.baseUrl.trim());
    this.config.setApiKey(this.apiKey.trim());
    this.panelOpen = false;
  }
}
