using System;

namespace ValheimMetrics.Map
{
    // Tipo da peca no mapa: material do WearNTear para o que e construcao; o resto por funcao.
    // O numero vai para o pieces.bin: so acrescentar no fim.
    public enum PieceKind : byte
    {
        Wood,
        HardWood,
        Timberwood,
        Stone,
        Marble,
        Grausten,
        Iron,
        Ancient,
        Ice,
        Furniture,
        Crop,
        Ship,
    }

    // Uma peca vista de cima: retangulo no centro X/Z, girado pelo yaw, com meia largura nos eixos
    // locais x e z do prefab. Y e a altura, para o que esta mais alto (telhado) cobrir o de baixo.
    public struct PieceMark
    {
        public float X;
        public float Z;
        public float Y;
        public float Cos;
        public float Sin;
        public float HalfX;
        public float HalfZ;
        public PieceKind Kind;

        public PieceMark(PieceKind kind, float x, float y, float z, float yawDegrees, float halfX, float halfZ)
        {
            Kind = kind;
            X = x;
            Y = y;
            Z = z;
            double yaw = yawDegrees * Math.PI / 180.0;
            Cos = (float)Math.Cos(yaw);
            Sin = (float)Math.Sin(yaw);
            HalfX = halfX;
            HalfZ = halfZ;
        }
    }
}
