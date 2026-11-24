import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { viteSingleFile } from "vite-plugin-singlefile";

// One self-contained index.html: embedded into the add-in DLL and served offline.
export default defineConfig({
  plugins: [react(), viteSingleFile()],
  build: { target: "es2020", cssCodeSplit: false, assetsInlineLimit: 100000000, outDir: "dist", emptyOutDir: true },
  server: { port: 5178, strictPort: true },
});
