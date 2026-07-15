export interface ManagedWindow {
  isDestroyed(): boolean;
  isMinimized(): boolean;
  show(): void;
  restore(): void;
  focus(): void;
  once(event: "closed", listener: () => void): this;
}

export class SingleInstanceWindow<TWindow extends ManagedWindow> {
  private window: TWindow | null = null;

  constructor(private readonly createWindow: () => TWindow) {}

  current(): TWindow | null {
    if (this.window?.isDestroyed()) this.window = null;
    return this.window;
  }

  getOrCreate(): TWindow {
    const currentWindow = this.current();
    if (currentWindow !== null) return currentWindow;

    const createdWindow = this.createWindow();
    this.window = createdWindow;
    createdWindow.once("closed", () => {
      if (this.window === createdWindow) this.window = null;
    });
    return createdWindow;
  }

  show(): TWindow {
    const activeWindow = this.getOrCreate();
    if (activeWindow.isMinimized()) activeWindow.restore();
    activeWindow.show();
    activeWindow.focus();
    return activeWindow;
  }
}
