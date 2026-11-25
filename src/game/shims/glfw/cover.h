// A window that covers its screen (cover.m): the menu bar and Dock hidden over Borderless, Fit Below the Notch
#pragma once

#include <GLFW/glfw3.h>

// After the window was placed (glfwSetWindowMonitor): whether it covers a screen now
void coverUpdate(GLFWwindow* window);
// From glfwPollEvents, four times a second: the window placed last again, after GLFW or a display change moved it
void coverCheck(void);
// Before the window is destroyed
void coverForget(GLFWwindow* window);
