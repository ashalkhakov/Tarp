namespace Tarp.Types;

public static class DoublyLinkedList
{
    public static int Length<T>(LinkedListNode<T>? list)
    {
        var head = list;
        int len = 0;

        while (head != null)
        {
            len++;
            head = head.Next;
        }

        return len;
    }

    public static void Split<T>(ref LinkedListNode<T>? list, out LinkedListNode<T?> tail)
    {
        if (list == null || list.Next == null)
        {
            tail = null;
            return;
        }
        tail = list.Next;

        tail.Prev = null;
        list.Next = null;
    }

    public static void InsertBefore<T>(ref LinkedListNode<T>? list, T item)
    {
        var newNode = new LinkedListNode<T>(item);

        if (list == null)
        {
            list = newNode;
            return;
        }

        newNode.Prev = list.Prev;
        list.Prev = newNode;
        newNode.Next = list;

        if (newNode.Prev != null)
        {
            newNode.Prev.Next = newNode;
        }
        list = newNode;
    }

    public static void InsertAfter<T>(ref LinkedListNode<T>? list, T item)
    {
        var newNode = new LinkedListNode<T>(item);

        if (list == null)
        {
            list = newNode;
            return;
        }

        newNode.Next = list.Next;
        list.Next = newNode;
        newNode.Prev = list;
        if (newNode.Next != null)
        {
            newNode.Next.Prev = newNode;
        }
        list = newNode;
    }

    public static void AppendToEnd<T>(ref LinkedListNode<T>? list, T item)
    {
        if (list == null)
        {
            var tmp = new LinkedListNode<T>(item);
            tmp.Next = null;
            list = tmp;
        }
        else
        {
            var head = list;
            while (head.Next != null)
                head = head.Next;
            var tmp = new LinkedListNode<T>(item);
            tmp.Next = null;
            head.Next = tmp;
        }
    }

    public static LinkedListNode<T>? Reverse<T>(LinkedListNode<T>? list)
    {
        if (list == null)
        {
            return null;
        }

        LinkedListNode<T>? prev = null;
        var current = list;
        LinkedListNode<T>? next;
        while (current != null)
        {
            next = current.Next;
            current.Next = prev;
            prev = current;
            current = next;
        }
        return prev;
    }
}
