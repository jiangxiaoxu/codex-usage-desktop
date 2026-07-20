export const STARTUP_LAUNCH_ARGUMENT = "--startup";

export function isStartupLaunch(commandLine: readonly string[]): boolean {
  return commandLine.includes(STARTUP_LAUNCH_ARGUMENT);
}
