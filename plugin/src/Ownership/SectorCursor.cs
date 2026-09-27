using System;
using System.Collections.Generic;

namespace ValheimMetrics.Ownership
{
    // Percorre os baldes por setor do ZDOMan aos pedacos, retomando do ponto exato no frame seguinte.
    // Os baldes mudam entre frames: indice alem do fim so encerra o setor, e item trocado de lugar
    // pode ser visto duas vezes ou nenhuma naquela volta. Para contagem, basta.
    sealed class SectorCursor<T>
    {
        readonly List<T>[] _sectors;
        readonly int _checkEvery;
        int _sector;
        int _index;

        public SectorCursor(List<T>[] sectors, int checkEvery)
        {
            _sectors = sectors;
            _checkEvery = Math.Max(1, checkEvery);
        }

        // Devolve true quando a volta fecha; a proxima chamada recomeca do primeiro setor.
        public bool Step(Func<bool> overBudget, Action<T> visit)
        {
            int sinceCheck = 0;
            while (_sector < _sectors.Length)
            {
                var list = _sectors[_sector];
                if (list != null)
                {
                    while (_index < list.Count)
                    {
                        visit(list[_index++]);
                        if (++sinceCheck >= _checkEvery)
                        {
                            sinceCheck = 0;
                            if (overBudget())
                                return false;
                        }
                    }
                }
                _sector++;
                _index = 0;
            }
            _sector = 0;
            return true;
        }
    }
}
