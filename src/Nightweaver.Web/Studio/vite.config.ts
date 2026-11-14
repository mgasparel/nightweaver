import { defineConfig } from "vite";

export default defineConfig({
  base: "/studio/",
  build: {
    outDir: "../wwwroot/studio",
    emptyOutDir: true,
  },
});
