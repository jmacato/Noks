// Keep this module path stable for the browser host.
// Avalonia schedules the frames. Browsers pace requestAnimationFrame to the display.
// They also throttle it while the page is hidden.
// A replacement for TimerHelper.runAnimationFrames can prevent the first paint.
export * from './_framework/avalonia.js'
