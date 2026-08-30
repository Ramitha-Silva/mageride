import { react } from '@mageride/eslint-config';

export default [
  ...react,
  {
    ignores: ['.next/**', 'next-env.d.ts'],
  },
];
