# The GLFW shim

`libGlfwShim.dylib` fixes a few of GLFW's calls on macOS without modifying GLFW (RocketWerkz's build, from BRUTAL's
native packages). The launcher loads it with `DYLD_INSERT_LIBRARIES`, and dyld interposing puts its functions in the
place of GLFW's and macOS's. [`glfw_shim.c`](glfw_shim.c) explains each one, and [`cover.m`](cover.m) places a window that
covers its screen:

| Call | Problem | Fix |
|---|---|---|
| `CGDisplayCopyAllDisplayModes` | GLFW asks macOS for display modes without the scaled HiDPI ones, so the resolution list on a MacBook display lacks sizes like 1352x878 and 1147x745 | asks for them, and keeps those of the desktop's kind and shape |
| `glfwSetWindowMonitor` | in Fullscreen the game always asks for the desktop's size, so the Resolution setting does nothing there | uses the Resolution from `settings.toml`, and sizes the window again when it leaves Fullscreen |
| `glfwSetWindowMonitor`, `glfwPollEvents` | GLFW's macOS backend doesn't report the window's new position after a display mode change, so dropdowns open in the wrong spot | reports it |
| `glfwSetInputMode` | the game turns raw mouse motion off without checking whether it is supported, and GLFW logs an error after every right-drag | skips those calls |
| `CGDisplayIsAsleep` | GLFW treats a sleeping display as unplugged, and dropping the monitor can free it twice, which crashes the game in Fullscreen when the display goes to sleep | tells GLFW that no display is asleep |
| `glfwSetWindowMonitor`, `glfwPollEvents`, `glfwDestroyWindow` | Borderless covers the whole screen at the normal window level, so the menu bar and the Dock are drawn over the game | hides both until the pointer reaches them, while an undecorated window covers its screen ([`cover.m`](cover.m)) |
| `glfwSetWindowMonitor`, `glfwPollEvents`, `glfwDestroyWindow` | on a display with a camera notch, Borderless and Fullscreen put the game's top row beside the camera | with Fit Below the Notch, moves the window below the notch and fills the screen behind it with black ([`cover.m`](cover.m)) |
