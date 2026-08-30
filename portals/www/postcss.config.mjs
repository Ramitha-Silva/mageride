/**
 * AL-52: "CSS is compiled at build time by PostCSS inside `npm run build`".
 * This file is that sentence. One plugin, no runtime style injection.
 */
const config = {
  plugins: {
    '@tailwindcss/postcss': {},
  },
};

export default config;
